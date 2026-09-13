using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentFoundry.Desktop;

public sealed record PaneInfo(string Id, string ProfileName, string ProjectName, string WorkingDirectory,
    string Mode, string? SourceSessionId, string? SessionId, string Status, int? ExitCode,
    DateTimeOffset CreatedAt);

internal readonly record struct ReplaySnapshot(byte[] Bytes, bool Truncated);

internal sealed class ReplayTail
{
    readonly byte[] _buffer;
    readonly object _gate = new();
    int _start;
    int _count;
    bool _truncated;

    public ReplayTail(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new byte[capacity];
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (data.Length >= _buffer.Length)
            {
                data[^_buffer.Length..].CopyTo(_buffer);
                _start = 0;
                _count = _buffer.Length;
                _truncated = true;
                return;
            }
            int overflow = Math.Max(0, _count + data.Length - _buffer.Length);
            if (overflow > 0)
            {
                _start = (_start + overflow) % _buffer.Length;
                _count -= overflow;
                _truncated = true;
            }
            int end = (_start + _count) % _buffer.Length;
            int first = Math.Min(data.Length, _buffer.Length - end);
            data[..first].CopyTo(_buffer.AsSpan(end));
            data[first..].CopyTo(_buffer);
            _count += data.Length;
        }
    }

    public ReplaySnapshot Snapshot()
    {
        lock (_gate)
        {
            byte[] result = new byte[_count];
            int first = Math.Min(_count, _buffer.Length - _start);
            _buffer.AsSpan(_start, first).CopyTo(result);
            _buffer.AsSpan(0, _count - first).CopyTo(result.AsSpan(first));
            return new(result, _truncated);
        }
    }
}

internal sealed class TerminalRateGate
{
    readonly object _gate = new();
    readonly int _limit;
    readonly TimeSpan _window;
    DateTimeOffset _started;
    int _used;

    public TerminalRateGate(int limit, TimeSpan window)
    {
        if (limit <= 0 || window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
        _window = window;
    }

    public bool TryTake(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_started == default || now - _started >= _window || now < _started)
            {
                _started = now;
                _used = 0;
            }
            if (_used >= _limit) return false;
            _used++;
            return true;
        }
    }
}

internal sealed class AttachmentLease : IDisposable
{
    readonly CancellationTokenSource _cancel = new();
    bool _disposed;
    public AttachmentLease(string generation) => Generation = generation;
    public string Generation { get; }
    public CancellationToken CancellationToken => _cancel.Token;
    public bool IsCancelled => _cancel.IsCancellationRequested;
    public void Revoke() { if (!_disposed) _cancel.Cancel(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancel.Cancel();
        _cancel.Dispose();
    }
}

internal sealed class AttachmentGeneration
{
    readonly object _gate = new();
    AttachmentLease? _current;
    bool _closed;

    public bool TryClaim(string generation, out AttachmentLease lease)
    {
        TerminalBoundary.Id(generation);
        lock (_gate)
        {
            if (_closed) { lease = null!; return false; }
            _current?.Revoke();
            lease = _current = new AttachmentLease(generation);
            return true;
        }
    }

    public bool IsCurrent(string generation)
    {
        lock (_gate) return !_closed && _current is { IsCancelled: false } item && item.Generation == generation;
    }

    public void Release(string generation)
    {
        lock (_gate)
        {
            if (_current?.Generation != generation) return;
            _current.Revoke();
            _current = null;
        }
    }

    public void Revoke()
    {
        lock (_gate)
        {
            _closed = true;
            _current?.Revoke();
            _current = null;
        }
    }
}

public sealed class TerminalServer : IAsyncDisposable
{
    public const string AuthorizationHeader = "X-Foundry-Terminal";
    static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(3);
    readonly object _gate = new();
    readonly string _secret;
    readonly string _workerExe;
    readonly string[] _workerArgsPrefix;
    readonly SafeJob _lifetimeJob;
    readonly AttachmentTickets _tickets = new();
    readonly Dictionary<string, WorkerPane> _panes = new(StringComparer.Ordinal);
    readonly HashSet<TaskCompletionSource> _pending = [];
    readonly CancellationTokenSource _shutdown = new();
    readonly Dictionary<string, StaticAsset> _assets;
    WebApplication? _app;
    string? _origin;
    string _state = "running";
    DateTimeOffset? _quiesceDeadline;
    Task? _dispose;

    TerminalServer(string secret, string workerExe, IReadOnlyList<string> workerArgsPrefix,
        SafeJob lifetimeJob, Dictionary<string, StaticAsset> assets)
    {
        _secret = secret;
        _workerExe = workerExe;
        _workerArgsPrefix = workerArgsPrefix.ToArray();
        _lifetimeJob = lifetimeJob;
        _assets = assets;
    }

    public string Origin => Volatile.Read(ref _origin) ?? throw new InvalidOperationException("Terminal service has not started.");

    public IReadOnlyList<PaneInfo> Panes
    {
        get { lock (_gate) return _panes.Values.Select(x => x.Snapshot).OrderBy(x => x.CreatedAt).ToArray(); }
    }

    public event Action? PanesChanged;

    public static async Task<TerminalServer> StartAsync(string assetRoot, string secret, string workerExe,
        IReadOnlyList<string> workerArgsPrefix, SafeJob lifetimeJob)
    {
        ArgumentNullException.ThrowIfNull(workerArgsPrefix);
        ArgumentNullException.ThrowIfNull(lifetimeJob);
        if (secret.Length != 64 || secret.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("Terminal secret must be 32 random bytes encoded as lowercase hex.", nameof(secret));
        assetRoot = Path.GetFullPath(assetRoot);
        workerExe = Path.GetFullPath(workerExe);
        if (!Directory.Exists(assetRoot) || !File.Exists(workerExe)) throw new DirectoryNotFoundException("Terminal assets or worker are unavailable.");
        if (workerArgsPrefix.Count > 32 || workerArgsPrefix.Any(x => x is null || x.Length > 4096 || x.Contains('\0')))
            throw new ArgumentException("Worker arguments are invalid.", nameof(workerArgsPrefix));
        Dictionary<string, StaticAsset> assets = VerifyAssets(assetRoot);
        var server = new TerminalServer(secret, workerExe, workerArgsPrefix, lifetimeJob, assets);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.Configure<JsonOptions>(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = TerminalLimits.PrivateFrameBytes;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.MaxConcurrentUpgradedConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        WebApplication app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Run(server.HandleAsync);
        try
        {
            await app.StartAsync().ConfigureAwait(false);
            IServer serverFeature = app.Services.GetRequiredService<IServer>();
            string address = serverFeature.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Kestrel did not publish its loopback address.");
            var uri = new Uri(address);
            if (uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out IPAddress? ip) || !IPAddress.IsLoopback(ip))
                throw new InvalidOperationException("Terminal service did not bind only to loopback.");
            Volatile.Write(ref server._origin, uri.GetLeftPart(UriPartial.Authority));
            server._app = app;
            return server;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> LaunchAsync(TerminalLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        byte[] launchPayload = WorkerFrames.EncodeLaunch(launch);
        WorkerPane? pane = null;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_state != "running") throw new InvalidOperationException("Terminal is shutting down.");
            if (_panes.Count + _pending.Count >= TerminalLimits.MaxPanes)
                throw new InvalidOperationException("The eight-pane terminal limit has been reached.");
            _pending.Add(pending);
        }
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(5));
        OwnedProcess? process = null;
        try
        {
            string id = Guid.NewGuid().ToString();
            var args = new List<string>(_workerArgsPrefix) { "--terminal-worker" };
            process = OwnedProcess.Start(_workerExe, args, Path.GetDirectoryName(_workerExe)!,
                WorkerEnvironment(), _lifetimeJob);
            pane = await WorkerPane.StartAsync(id, launch, launchPayload, process, PaneExited, startup.Token)
                .ConfigureAwait(false);
            process = null;
            lock (_gate)
            {
                if (_state != "running") throw new InvalidOperationException("Terminal is shutting down.");
                _panes.Add(id, pane);
            }
            RaisePanesChanged();
            return id;
        }
        catch
        {
            if (pane is not null) await pane.CloseAsync(CurrentCloseDeadline()).ConfigureAwait(false);
            else if (process is not null)
            {
                try { process.Stop(); } catch { }
                await process.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            lock (_gate) _pending.Remove(pending);
            pending.TrySetResult();
        }
    }

    public async Task ClosePaneAsync(string id)
    {
        TerminalBoundary.Id(id);
        WorkerPane pane;
        lock (_gate)
        {
            if (_state != "running") throw new InvalidOperationException("Terminal is shutting down.");
            if (!_panes.Remove(id, out pane!)) throw new KeyNotFoundException("Terminal pane was not found.");
        }
        await pane.CloseAsync(DateTimeOffset.UtcNow + ShutdownDeadline).ConfigureAwait(false);
        RaisePanesChanged();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_dispose ??= DisposeCoreAsync());
    }

    async Task DisposeCoreAsync()
    {
        WorkerPane[] panes;
        Task[] pending;
        DateTimeOffset deadlineAt = DateTimeOffset.UtcNow + ShutdownDeadline;
        lock (_gate)
        {
            if (_state == "closed") return;
            _state = "quiescing";
            _quiesceDeadline = deadlineAt;
            _shutdown.Cancel();
            _tickets.RevokeAll();
            panes = _panes.Values.ToArray();
            _panes.Clear();
            pending = _pending.Select(x => x.Task).ToArray();
        }
        Task cleanup = Task.WhenAll(panes.Select(x => x.CloseAsync(deadlineAt)).Concat(pending));
        await WaitUntilAsync(cleanup, deadlineAt).ConfigureAwait(false);
        WebApplication? app = _app;
        if (app is not null)
        {
            TimeSpan remaining = deadlineAt - DateTimeOffset.UtcNow;
            using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            try { await app.StopAsync(deadline.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            await app.DisposeAsync().ConfigureAwait(false);
        }
        lock (_gate) _state = "closed";
        _shutdown.Dispose();
        RaisePanesChanged();
    }

    async Task HandleAsync(HttpContext context)
    {
        try { await HandleCoreAsync(context).ConfigureAwait(false); }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = error.StatusCode;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or KeyNotFoundException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = 400;
        }
        catch (InvalidOperationException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status409Conflict;
        }
    }

    async Task HandleCoreAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        string? publishedOrigin = Volatile.Read(ref _origin);
        string state; lock (_gate) state = _state;
        if (publishedOrigin is null || state != "running")
        { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        string authority = new Uri(publishedOrigin).Authority;
        string[] hosts = context.Request.Headers.Host.Select(x => x!).ToArray();
        string[] origins = context.Request.Headers.Origin.Select(x => x!).ToArray();
        if (context.Request.Path == "/connect")
        {
            if (!TerminalBoundary.WebSocket(hosts, origins, authority)) { context.Response.StatusCode = 403; return; }
            await HandleWebSocketAsync(context).ConfigureAwait(false);
            return;
        }
        string[] keys = context.Request.Headers[AuthorizationHeader].Select(x => x!).ToArray();
        if (!TerminalBoundary.Http(hosts, origins, keys, authority, _secret, context.Request.Method))
        { context.Response.StatusCode = 403; return; }
        if (context.Request.QueryString.HasValue) { context.Response.StatusCode = 400; return; }

        string path = context.Request.Path.Value ?? "";
        if (HttpMethods.IsGet(context.Request.Method) && _assets.TryGetValue(path, out StaticAsset? asset))
        {
            context.Response.ContentType = asset.ContentType;
            context.Response.ContentLength = asset.Content.Length;
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            await context.Response.Body.WriteAsync(asset.Content).ConfigureAwait(false);
            return;
        }
        if (HttpMethods.IsGet(context.Request.Method) && path == "/api/panes")
        {
            await context.Response.WriteAsJsonAsync(new { panes = Panes, state, maxPanes = TerminalLimits.MaxPanes }).ConfigureAwait(false);
            return;
        }
        if (HttpMethods.IsPost(context.Request.Method) && path == "/api/ticket")
        {
            byte[] body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using JsonDocument document = TerminalBoundary.Parse(body, ["paneId", "generation"]);
            string paneId = TerminalBoundary.Id(JsonString(document.RootElement, "paneId"));
            string generation = TerminalBoundary.Id(JsonString(document.RootElement, "generation"));
            lock (_gate)
                if (_state != "running" || !_panes.TryGetValue(paneId, out WorkerPane? pane) || !pane.IsRunning)
                { context.Response.StatusCode = 404; return; }
            string ticket = _tickets.Mint(paneId, generation, DateTimeOffset.UtcNow);
            await context.Response.WriteAsJsonAsync(new { ticket, expiresInSeconds = 20 }).ConfigureAwait(false);
            return;
        }
        if (HttpMethods.IsPost(context.Request.Method) && TryClosePath(path, out string? closeId))
        {
            byte[] body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using JsonDocument document = TerminalBoundary.Parse(body, ["confirmed"]);
            if (document.RootElement.GetProperty("confirmed").ValueKind != JsonValueKind.True)
            { context.Response.StatusCode = 400; return; }
            try { await ClosePaneAsync(closeId).ConfigureAwait(false); }
            catch (KeyNotFoundException) { context.Response.StatusCode = 404; return; }
            context.Response.StatusCode = 204;
            return;
        }
        context.Response.StatusCode = 404;
    }

    async Task HandleWebSocketAsync(HttpContext context)
    {
        if (context.Request.QueryString.HasValue || !context.WebSockets.IsWebSocketRequest)
        { context.Response.StatusCode = 400; return; }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var firstFrameDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        firstFrameDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        WorkerPane? pane = null;
        PaneAttachment? attachment = null;
        try
        {
            byte[] first = await ReceiveTextAsync(socket, TerminalLimits.PrivateFrameBytes, firstFrameDeadline.Token)
                .ConfigureAwait(false);
            using JsonDocument document = TerminalBoundary.Parse(first, ["paneId", "generation", "ticket"]);
            string paneId = TerminalBoundary.Id(JsonString(document.RootElement, "paneId"));
            string generation = TerminalBoundary.Id(JsonString(document.RootElement, "generation"));
            string ticket = JsonString(document.RootElement, "ticket");
            if (ticket.Length != 64 || !_tickets.Consume(ticket, paneId, generation, DateTimeOffset.UtcNow))
                throw new InvalidDataException("Attachment ticket was rejected.");
            lock (_gate)
                if (_state != "running" || !_panes.TryGetValue(paneId, out pane) || !pane.IsRunning)
                    throw new InvalidDataException("Pane is unavailable.");
            attachment = pane.Attach(generation);
            using var attached = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
                attachment.CancellationToken);
            Task sender = SendAttachmentAsync(socket, attachment, attached.Token);
            Task receiver = ReceiveCommandsAsync(socket, pane, attachment, attached.Token);
            await Task.WhenAny(sender, receiver, attachment.PolicyCloseRequested).ConfigureAwait(false);
            if (attachment.PolicyCloseRequested.IsCompleted && !sender.IsCompleted)
            {
                if (await Task.WhenAny(sender, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false) != sender)
                    socket.Abort();
            }
            else if (sender.IsCompletedSuccessfully && socket.State == WebSocketState.Open)
                await SafeCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "Terminal exited").ConfigureAwait(false);
            attached.Cancel();
            try { await Task.WhenAll(sender, receiver).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or ChannelClosedException) { }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or WebSocketException)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await SafeCloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Terminal attachment rejected").ConfigureAwait(false);
        }
        finally
        {
            if (pane is not null && attachment is not null)
            {
                pane.Detach(attachment);
                attachment.Dispose();
            }
            if (socket.State == WebSocketState.Open)
            {
                bool slow = attachment?.CloseReason == "Terminal client too slow";
                await SafeCloseAsync(socket, slow ? WebSocketCloseStatus.PolicyViolation : WebSocketCloseStatus.NormalClosure,
                    slow ? "Terminal client too slow" : "Detached").ConfigureAwait(false);
            }
        }
    }

    static async Task SendAttachmentAsync(WebSocket socket, PaneAttachment attachment, CancellationToken cancellationToken)
    {
        byte[] ready = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "ready",
            truncated = attachment.Replay.Truncated,
            historyUnavailable = attachment.Replay.Truncated
        });
        await socket.SendAsync(ready, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        byte[] replay = attachment.Replay.Bytes;
        for (int offset = 0; offset < replay.Length && !attachment.PolicyCloseRequested.IsCompleted;
             offset += TerminalLimits.OutputChunkBytes)
        {
            int count = Math.Min(TerminalLimits.OutputChunkBytes, replay.Length - offset);
            await socket.SendAsync(replay.AsMemory(offset, count), WebSocketMessageType.Binary, true, cancellationToken)
                .ConfigureAwait(false);
        }
        while (!attachment.PolicyCloseRequested.IsCompleted &&
               await attachment.Output.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            while (!attachment.PolicyCloseRequested.IsCompleted && attachment.TryRead(out byte[]? chunk))
                await socket.SendAsync(chunk, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        if (attachment.PolicyCloseRequested.IsCompleted)
        {
            while (attachment.TryRead(out _)) { }
            await SafeCloseAsync(socket, WebSocketCloseStatus.PolicyViolation,
                attachment.CloseReason ?? "Terminal client too slow").ConfigureAwait(false);
        }
    }

    static async Task ReceiveCommandsAsync(WebSocket socket, WorkerPane pane, PaneAttachment attachment,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] payload = await ReceiveTextAsync(socket, TerminalLimits.PrivateFrameBytes, cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument loose = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            if (loose.RootElement.ValueKind != JsonValueKind.Object ||
                !loose.RootElement.TryGetProperty("kind", out JsonElement kindValue) || kindValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid terminal command.");
            string? kind = kindValue.GetString();
            bool accepted;
            if (kind == "input")
            {
                using JsonDocument exact = TerminalBoundary.Parse(payload, ["kind", "data"]);
                string data = JsonString(exact.RootElement, "data");
                byte[] bytes = Encoding.UTF8.GetBytes(data);
                accepted = bytes.Length <= TerminalLimits.InputBytes && pane.TryInput(attachment, bytes);
            }
            else if (kind == "resize")
            {
                using JsonDocument exact = TerminalBoundary.Parse(payload, ["kind", "columns", "rows"]);
                int columns = JsonInt32(exact.RootElement, "columns");
                int rows = JsonInt32(exact.RootElement, "rows");
                accepted = columns is >= 2 and <= 500 && rows is >= 2 and <= 300 &&
                    pane.TryResize(attachment, (short)columns, (short)rows);
            }
            else throw new InvalidDataException("Unknown terminal command.");
            if (!accepted) throw new InvalidDataException("Terminal command was rate-limited, stale, or backpressured.");
        }
    }

    static async Task<byte[]> ReceiveTextAsync(WebSocket socket, int maximum, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(Math.Min(maximum, 4096));
        byte[] buffer = new byte[4096];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) throw new OperationCanceledException();
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Text frame required.");
            if (memory.Length + result.Count > maximum) throw new InvalidDataException("WebSocket frame too large.");
            memory.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return memory.ToArray();
        }
    }

    static async Task SafeCloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await socket.CloseOutputAsync(status, reason, deadline.Token).ConfigureAwait(false); }
        catch { socket.Abort(); }
    }

    static string JsonString(JsonElement item, string name)
    {
        JsonElement value = item.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("String value required.");
        return value.GetString() ?? throw new JsonException("Non-null string required.");
    }

    static int JsonInt32(JsonElement item, string name)
    {
        JsonElement value = item.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new JsonException("32-bit integer required.");
        return result;
    }

    static async Task<bool> WaitUntilAsync(Task task, DateTimeOffset deadline)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return task.IsCompleted;
        if (await Task.WhenAny(task, Task.Delay(remaining)).ConfigureAwait(false) != task) return false;
        try { await task.ConfigureAwait(false); } catch { }
        return true;
    }

    static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength > TerminalLimits.PrivateFrameBytes)
            throw new Microsoft.AspNetCore.Http.BadHttpRequestException("Request body exceeds 64 KiB.", StatusCodes.Status413PayloadTooLarge);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await request.Body.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return memory.ToArray();
            if (memory.Length + count > TerminalLimits.PrivateFrameBytes)
                throw new Microsoft.AspNetCore.Http.BadHttpRequestException("Request body exceeds 64 KiB.", StatusCodes.Status413PayloadTooLarge);
            memory.Write(buffer, 0, count);
        }
    }

    static bool TryClosePath(string path, out string id)
    {
        const string prefix = "/api/panes/";
        const string suffix = "/close";
        if (path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal))
        {
            string candidate = path[prefix.Length..^suffix.Length];
            try { id = TerminalBoundary.Id(candidate); return true; } catch (ArgumentException) { }
        }
        id = "";
        return false;
    }

    void PaneExited(WorkerPane pane)
    {
        lock (_gate) if (!_panes.TryGetValue(pane.Id, out WorkerPane? current) || current != pane) return;
        RaisePanesChanged();
    }

    void RaisePanesChanged()
    {
        try { PanesChanged?.Invoke(); } catch { }
    }

    DateTimeOffset CurrentCloseDeadline()
    {
        lock (_gate) return _quiesceDeadline ?? DateTimeOffset.UtcNow + ShutdownDeadline;
    }

    static Dictionary<string, string> WorkerEnvironment()
    {
        string[] allowed = ["SystemRoot", "WINDIR", "TEMP", "TMP"];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in allowed)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) result[name] = value;
        }
        return result;
    }

    static Dictionary<string, StaticAsset> VerifyAssets(string root)
    {
        var result = new Dictionary<string, StaticAsset>(StringComparer.Ordinal)
        {
            ["/"] = Asset(root, "index.html", "text/html; charset=utf-8"),
            ["/index.html"] = Asset(root, "index.html", "text/html; charset=utf-8"),
            ["/terminal.js"] = Asset(root, "terminal.js", "text/javascript; charset=utf-8"),
            ["/terminal.css"] = Asset(root, "terminal.css", "text/css; charset=utf-8"),
        };
        string manifestPath = Path.Combine(root, "vendor", "manifest.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > TerminalLimits.PrivateFrameBytes)
            throw new InvalidDataException("Terminal vendor manifest is missing or exceeds 64 KiB.");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement package in manifest.RootElement.EnumerateArray())
            foreach (JsonProperty file in package.GetProperty("files").EnumerateObject())
                if (!hashes.TryAdd(file.Name, file.Value.GetString() ?? ""))
                    throw new InvalidDataException("Duplicate terminal vendor asset manifest entry.");
        foreach ((string name, string type) in new[]
        {
            ("xterm.js", "text/javascript; charset=utf-8"), ("xterm.css", "text/css; charset=utf-8"),
            ("addon-fit.js", "text/javascript; charset=utf-8")
        })
        {
            if (!hashes.TryGetValue(name, out string? expected) || expected.Length != 64)
                throw new InvalidDataException("Required terminal vendor asset is not pinned.");
            StaticAsset asset = Asset(Path.Combine(root, "vendor"), name, type);
            string actual = Convert.ToHexString(SHA256.HashData(asset.Content)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
                throw new InvalidDataException("Terminal vendor asset hash mismatch.");
            result["/vendor/" + name] = asset;
        }
        return result;
    }

    static StaticAsset Asset(string root, string relative, string contentType)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Terminal static asset is unavailable.", relative);
        if (new FileInfo(path).Length > 4L * 1024 * 1024)
            throw new InvalidDataException("Terminal static asset exceeds 4 MiB.");
        return new(File.ReadAllBytes(path), contentType);
    }

    sealed record StaticAsset(byte[] Content, string ContentType);
}

internal sealed class WorkerPane
{
    readonly object _gate = new();
    readonly OwnedProcess _process;
    readonly Channel<WorkerCommand> _commands;
    readonly ReplayTail _replay = new(TerminalLimits.ReplayBytes);
    readonly TerminalRateGate _inputRate = new(128, TimeSpan.FromSeconds(1));
    readonly TerminalRateGate _resizeRate = new(20, TimeSpan.FromSeconds(1));
    readonly AttachmentGeneration _generations = new();
    readonly Action<WorkerPane> _onExit;
    Task? _writer;
    Task? _drain;
    Task? _watch;
    Task? _close;
    PaneAttachment? _attachment;
    string _status = "running";
    int? _exitCode;

    WorkerPane(string id, TerminalLaunch launch, OwnedProcess process, Action<WorkerPane> onExit)
    {
        Id = id;
        Launch = launch;
        _process = process;
        _onExit = onExit;
        CreatedAt = DateTimeOffset.UtcNow;
        _commands = Channel.CreateBounded<WorkerCommand>(new BoundedChannelOptions(TerminalLimits.PrivateQueueFrames)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    }

    public string Id { get; }
    public TerminalLaunch Launch { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool IsRunning { get { lock (_gate) return _status == "running"; } }

    public PaneInfo Snapshot
    {
        get
        {
            lock (_gate)
            {
                string project = Path.GetFileName(Launch.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                return new(Id, Launch.ProfileName, project, Launch.WorkingDirectory, Launch.Mode,
                    Launch.SourceSessionId, Launch.SessionId, _status, _exitCode, CreatedAt);
            }
        }
    }

    public static async Task<WorkerPane> StartAsync(string id, TerminalLaunch launch, byte[] launchPayload,
        OwnedProcess process, Action<WorkerPane> onExit, CancellationToken cancellationToken)
    {
        var pane = new WorkerPane(id, launch, process, onExit);
        try
        {
            await WorkerFrames.WriteEncodedLaunchAsync(process.StandardInput, launchPayload, cancellationToken).ConfigureAwait(false);
            pane._writer = pane.WriteCommandsAsync();
            pane._drain = pane.DrainOutputAsync();
            pane._watch = pane.WatchExitAsync();
            return pane;
        }
        catch
        {
            try { process.Stop(); } catch { }
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public PaneAttachment Attach(string generation)
    {
        TerminalBoundary.Id(generation);
        lock (_gate)
        {
            if (_status != "running") throw new InvalidOperationException("Pane is not running.");
            if (!_generations.TryClaim(generation, out AttachmentLease lease))
                throw new InvalidOperationException("Pane is not accepting attachments.");
            _attachment?.Cancel("Replaced by a newer attachment");
            ReplaySnapshot replay = _replay.Snapshot();
            return _attachment = new PaneAttachment(generation, replay, lease);
        }
    }

    public void Detach(PaneAttachment attachment)
    {
        lock (_gate)
        {
            if (_attachment != attachment) return;
            _attachment = null;
            _generations.Release(attachment.Generation);
            attachment.Cancel("Detached");
        }
    }

    public bool TryInput(PaneAttachment attachment, byte[] data)
    {
        lock (_gate)
        {
            if (data.Length > TerminalLimits.InputBytes || _status != "running" ||
                _attachment != attachment || attachment.IsCancelled || !_generations.IsCurrent(attachment.Generation) ||
                !_inputRate.TryTake(DateTimeOffset.UtcNow))
                return false;
            return _commands.Writer.TryWrite(WorkerCommand.Input(data));
        }
    }

    public bool TryResize(PaneAttachment attachment, short columns, short rows)
    {
        lock (_gate)
        {
            if (_status != "running" || _attachment != attachment || attachment.IsCancelled ||
                !_generations.IsCurrent(attachment.Generation) || !_resizeRate.TryTake(DateTimeOffset.UtcNow)) return false;
            return _commands.Writer.TryWrite(WorkerCommand.Resize(columns, rows));
        }
    }

    public Task CloseAsync(DateTimeOffset deadline)
    {
        lock (_gate) return _close ??= CloseCoreAsync(deadline);
    }

    async Task CloseCoreAsync(DateTimeOffset deadline)
    {
        lock (_gate)
        {
            if (_status == "running") _status = "closing";
            _attachment?.Cancel("Pane closed");
            _attachment = null;
            _generations.Revoke();
            _commands.Writer.TryComplete();
        }
        Task<int> exit = _process.WaitForExitAsync();
        if (!await WaitUntilAsync(exit, deadline).ConfigureAwait(false))
        {
            try { _process.Stop(); } catch { }
        }
        int? finalExit = exit.IsCompletedSuccessfully ? exit.Result : -1;
        await _process.DisposeAsync().ConfigureAwait(false);
        if (_writer is not null) await WaitUntilAsync(_writer, deadline).ConfigureAwait(false);
        if (_drain is not null) await WaitUntilAsync(_drain, deadline).ConfigureAwait(false);
        lock (_gate) if (_status != "exited") { _status = "exited"; _exitCode = finalExit; }
    }

    async Task WriteCommandsAsync()
    {
        try
        {
            await foreach (WorkerCommand command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (command.Kind == WorkerControlKind.Input)
                    await WorkerFrames.WriteInputAsync(_process.StandardInput, command.Data, CancellationToken.None).ConfigureAwait(false);
                else if (command.Kind == WorkerControlKind.Resize)
                    await WorkerFrames.WriteResizeAsync(_process.StandardInput, command.Columns, command.Rows, CancellationToken.None).ConfigureAwait(false);
            }
            await WorkerFrames.WriteCloseAsync(_process.StandardInput, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    async Task DrainOutputAsync()
    {
        byte[] buffer = new byte[TerminalLimits.OutputChunkBytes];
        try
        {
            while (true)
            {
                int count = await _process.StandardOutput.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                byte[] chunk = buffer.AsSpan(0, count).ToArray();
                lock (_gate)
                {
                    _replay.Append(chunk);
                    if (_attachment is { } attachment && !attachment.TryWrite(chunk))
                    {
                        attachment.RequestPolicyClose("Terminal client too slow");
                        _attachment = null;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        try
        {
            Task<int> exit = _process.WaitForExitAsync();
            if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromMilliseconds(250))).ConfigureAwait(false) != exit)
                _process.Stop();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    async Task WatchExitAsync()
    {
        int exit;
        try { exit = await _process.WaitForExitAsync().ConfigureAwait(false); }
        catch { exit = _process.ExitCode ?? -1; }
        if (_drain is not null) await _drain.ConfigureAwait(false);
        lock (_gate)
        {
            _status = "exited";
            _exitCode = exit;
            _commands.Writer.TryComplete();
            _attachment?.Complete();
        }
        _onExit(this);
    }

    static async Task<bool> WaitUntilAsync(Task task, DateTimeOffset deadline)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return task.IsCompleted;
        return await Task.WhenAny(task, Task.Delay(remaining)).ConfigureAwait(false) == task;
    }
}

internal sealed class PaneAttachment
{
    readonly object _gate = new();
    readonly CancellationTokenSource _cancel;
    readonly AttachmentLease _lease;
    readonly TaskCompletionSource _policyClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _queuedBytes;
    string? _closeReason;
    bool _cancelled;
    bool _disposed;

    public PaneAttachment(string generation, ReplaySnapshot replay, AttachmentLease lease)
    {
        Generation = generation;
        Replay = replay;
        _lease = lease;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken);
        Output = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    }

    public string Generation { get; }
    public ReplaySnapshot Replay { get; }
    public Channel<byte[]> Output { get; }
    public CancellationToken CancellationToken => _cancel.Token;
    public bool IsCancelled { get { lock (_gate) return _cancelled; } }
    public string? CloseReason { get { lock (_gate) return _closeReason; } }
    public Task PolicyCloseRequested => _policyClose.Task;

    public bool TryWrite(byte[] chunk)
    {
        lock (_gate)
        {
            if (_cancelled || _queuedBytes + chunk.Length > TerminalLimits.AttachmentBacklogBytes)
                return false;
            _queuedBytes += chunk.Length;
            if (Output.Writer.TryWrite(chunk)) return true;
            _queuedBytes -= chunk.Length;
            return false;
        }
    }

    public bool TryRead(out byte[] chunk)
    {
        if (!Output.Reader.TryRead(out chunk!)) return false;
        lock (_gate) _queuedBytes -= chunk.Length;
        return true;
    }

    public void Complete() => Output.Writer.TryComplete();

    public void RequestPolicyClose(string reason)
    {
        lock (_gate)
        {
            if (_cancelled) return;
            _cancelled = true;
            _closeReason = reason;
            Output.Writer.TryComplete();
            _policyClose.TrySetResult();
        }
    }

    public void Cancel(string reason)
    {
        lock (_gate)
        {
            _cancelled = true;
            _closeReason ??= reason;
            if (!_cancel.IsCancellationRequested) _cancel.Cancel();
            Output.Writer.TryComplete(new IOException(_closeReason));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Cancel(_closeReason ?? "Detached");
        _cancel.Dispose();
        _lease.Dispose();
    }
}
