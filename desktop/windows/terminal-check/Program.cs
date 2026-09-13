using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentFoundry.Desktop;

static void Check(bool value, string failure)
{
    if (!value) throw new Exception(failure);
}

static async Task Rejects(Func<Task> action, string failure)
{
    try { await action(); }
    catch (Exception error) when (error is ArgumentException or InvalidDataException or EndOfStreamException or System.Text.Json.JsonException)
    { return; }
    throw new Exception(failure);
}

static async Task RejectsUpgradeAsync(Uri uri, string? origin, string failure)
{
    using var socket = new ClientWebSocket();
    socket.Options.Proxy = null;
    if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try { await socket.ConnectAsync(uri, deadline.Token); }
    catch (WebSocketException) { return; }
    throw new Exception(failure);
}

static async Task TransportChecksAsync()
{
    string assetRoot = Path.Combine(AppContext.BaseDirectory, "terminal");
    string worker = Environment.ProcessPath ?? throw new Exception("test host executable unavailable");
    string secret = new('b', 64);
    await using TerminalServer server = await TerminalServer.StartAsync(assetRoot, secret, worker, [], new SafeJob());
    using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
    using var client = new HttpClient(handler) { BaseAddress = new Uri(server.Origin), Timeout = TimeSpan.FromSeconds(5) };

    using (HttpResponseMessage unauthorized = await client.GetAsync("/api/panes"))
        Check(unauthorized.StatusCode == HttpStatusCode.Forbidden, "unauthorized HTTP reached a terminal route");

    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/panes"))
    {
        request.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, secret);
        using HttpResponseMessage response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.OK, "authorized pane snapshot was rejected");
        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Check(payload.RootElement.GetProperty("maxPanes").GetInt32() == 8 &&
              payload.RootElement.GetProperty("state").GetString() == "running" &&
              payload.RootElement.GetProperty("panes").GetArrayLength() == 0,
            "pane snapshot did not match the zero-pane service contract");
    }

    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/panes"))
    {
        request.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, new[] { secret, secret });
        using HttpResponseMessage response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "duplicate HTTP authorization was accepted");
    }

    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/panes"))
    {
        request.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, new string('0', 64));
        using HttpResponseMessage response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "foreign HTTP authorization was accepted");
    }

    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/ticket"))
    {
        request.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, secret);
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        request.Content = new StringContent("this body must not be parsed", Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "foreign POST Origin was accepted");
    }

    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/panes?unexpected=1"))
    {
        request.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, secret);
        using HttpResponseMessage response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "unexpected HTTP query was accepted");
    }

    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/spawn"))
    {
        request.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, secret);
        request.Headers.TryAddWithoutValidation("Origin", server.Origin);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.NotFound, "an HTTP process-spawn route exists");
    }

    var webSocketUri = new Uri("ws" + server.Origin[4..] + "/connect");
    await RejectsUpgradeAsync(webSocketUri, null, "WebSocket upgrade without Origin was accepted");
    await RejectsUpgradeAsync(webSocketUri, "http://evil.example", "foreign WebSocket Origin was accepted");
    await RejectsUpgradeAsync(new Uri(webSocketUri + "?unexpected=1"), server.Origin,
        "WebSocket upgrade with a query was accepted");

    using (var socket = new ClientWebSocket())
    {
        socket.Options.Proxy = null;
        socket.Options.SetRequestHeader("Origin", server.Origin);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(webSocketUri, deadline.Token);
        byte[] invalidTicket = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            paneId = Guid.NewGuid().ToString(), generation = Guid.NewGuid().ToString(), ticket = new string('0', 64)
        }));
        await socket.SendAsync(invalidTicket, WebSocketMessageType.Text, true, deadline.Token);
        WebSocketReceiveResult result = await socket.ReceiveAsync(new byte[256], deadline.Token);
        Check(result.MessageType == WebSocketMessageType.Close && result.CloseStatus == WebSocketCloseStatus.PolicyViolation,
            "invalid first-frame ticket did not close the WebSocket with a policy rejection");
    }

    using (var uninitialized = new ClientWebSocket())
    {
        uninitialized.Options.Proxy = null;
        uninitialized.Options.SetRequestHeader("Origin", server.Origin);
        using var firstFrameDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        await uninitialized.ConnectAsync(webSocketUri, firstFrameDeadline.Token);
        var firstFrameTimer = Stopwatch.StartNew();
        bool disconnected = false;
        try
        {
            var result = await uninitialized.ReceiveAsync(new byte[256], firstFrameDeadline.Token);
            disconnected = result.MessageType == WebSocketMessageType.Close;
        }
        catch (WebSocketException error) when (error.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        { disconnected = true; }
        Check(disconnected && firstFrameTimer.Elapsed >= TimeSpan.FromSeconds(4) &&
              firstFrameTimer.Elapsed < TimeSpan.FromSeconds(7), "first-frame deadline did not disconnect the idle client independently of shutdown");
        Console.WriteLine($"Terminal first-frame timeout milliseconds: {firstFrameTimer.Elapsed.TotalMilliseconds:F0}");
    }

    using var idleSocket = new ClientWebSocket();
    idleSocket.Options.Proxy = null;
    idleSocket.Options.SetRequestHeader("Origin", server.Origin);
    using var idleDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await idleSocket.ConnectAsync(webSocketUri, idleDeadline.Token);
    var shutdown = Stopwatch.StartNew();
    await server.DisposeAsync();
    shutdown.Stop();
    Check(shutdown.Elapsed < TimeSpan.FromSeconds(4), "zero-pane service shutdown exceeded its bounded deadline");
    Console.WriteLine($"Terminal service shutdown milliseconds: {shutdown.Elapsed.TotalMilliseconds:F0}");
    await server.DisposeAsync();
}

try
{
Check(TerminalLimits.MaxPanes == 8 && TerminalLimits.ReplayBytes == 1024 * 1024 &&
      TerminalLimits.AttachmentBacklogBytes == 512 * 1024 && TerminalLimits.InputBytes == 8192 &&
      TerminalLimits.OutputChunkBytes == 65536, "published limits drifted");

var replay = new ReplayTail(10);
replay.Append(Encoding.ASCII.GetBytes("123456"));
replay.Append(Encoding.ASCII.GetBytes("789ABC"));
var snapshot = replay.Snapshot();
Check(Encoding.ASCII.GetString(snapshot.Bytes) == "3456789ABC" && snapshot.Truncated,
    "replay did not retain only the newest bounded bytes");

var limiter = new TerminalRateGate(2, TimeSpan.FromSeconds(1));
var instant = DateTimeOffset.UnixEpoch;
Check(limiter.TryTake(instant) && limiter.TryTake(instant) && !limiter.TryTake(instant),
    "rate gate did not reject the first excess operation");
Check(limiter.TryTake(instant.AddSeconds(1)), "rate gate did not reopen after its window");

var controlsStillOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Check(await WorkerLifetime.WaitAsync(controlsStillOpen.Task, Task.CompletedTask) == WorkerStopReason.ChildExited,
    "natural child exit did not end a worker whose private control pipe stayed open");

const string authority = "127.0.0.1:45678";
string secret = new('a', 64);
Check(TerminalBoundary.Http([authority], [], [secret], authority, secret, "GET"),
    "authorized exact-host navigation was rejected");
Check(TerminalBoundary.Http([authority], ["http://" + authority], [secret], authority, secret, "POST"),
    "authorized exact-origin mutation was rejected");
Check(!TerminalBoundary.Http([authority, authority], [], [secret], authority, secret, "GET") &&
      !TerminalBoundary.Http([authority], [], [secret, secret], authority, secret, "GET") &&
      !TerminalBoundary.Http([authority], ["null"], [secret], authority, secret, "POST") &&
      !TerminalBoundary.WebSocket([authority], ["http://evil.example"], authority),
    "duplicate or foreign Host/Origin/auth was accepted");

var tickets = new AttachmentTickets();
string ticketPane = Guid.NewGuid().ToString();
string ticketGeneration = Guid.NewGuid().ToString();
string ticket = tickets.Mint(ticketPane, ticketGeneration, instant);
bool[] consumers = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
    tickets.Consume(ticket, ticketPane, ticketGeneration, instant))));
Check(consumers.Count(x => x) == 1, "one-use attachment ticket was consumed more or less than once");
Check(!tickets.Consume(ticket, ticketPane, ticketGeneration, instant), "consumed attachment ticket replayed");
ticket = tickets.Mint(ticketPane, ticketGeneration, instant);
Check(!tickets.Consume(ticket, ticketPane, ticketGeneration, instant.AddSeconds(21)),
    "expired attachment ticket was accepted");

var backlogLease = new AttachmentLease(Guid.NewGuid().ToString());
var backlog = new PaneAttachment(backlogLease.Generation, new ReplaySnapshot([], false), backlogLease);
for (var index = 0; index < TerminalLimits.AttachmentBacklogBytes / TerminalLimits.OutputChunkBytes; index++)
    Check(backlog.TryWrite(new byte[TerminalLimits.OutputChunkBytes]), "bounded attachment backlog filled too early");
Check(!backlog.TryWrite([1]), "attachment backlog accepted more than 512 KiB");
Check(backlog.TryRead(out var drained) && drained.Length == TerminalLimits.OutputChunkBytes &&
      backlog.TryWrite([1]), "draining output did not release attachment backlog capacity");
backlog.Dispose();

var policyLease = new AttachmentLease(Guid.NewGuid().ToString());
var policyClose = new PaneAttachment(policyLease.Generation, new ReplaySnapshot([], false), policyLease);
CancellationToken policyIo = policyClose.CancellationToken;
Check(policyClose.TryWrite([1, 2, 3]), "policy-close fixture could not queue output");
policyClose.RequestPolicyClose("Terminal client too slow");
Check(policyClose.IsCancelled && policyClose.PolicyCloseRequested.IsCompleted &&
      policyClose.CloseReason == "Terminal client too slow" && !policyIo.IsCancellationRequested,
    "requesting a policy close did not preserve WebSocket I/O for the close-frame sender");
Check(!policyClose.TryWrite([4]), "policy-close attachment continued accepting output");
policyClose.Dispose();

var launch = new TerminalLaunch("Command Prompt", @"C:\Windows\System32\cmd.exe", new string('a', 64),
    ["/d"], @"C:\work", new() { ["SystemRoot"] = @"C:\Windows" }, "new", null, null);
await using (var stream = new MemoryStream())
{
    await WorkerFrames.WriteLaunchAsync(stream, launch, CancellationToken.None);
    Check(stream.Length <= TerminalLimits.PrivateFrameBytes + 8, "launch frame exceeded its wire bound");
    stream.Position = 0;
    TerminalLaunch decoded = await WorkerFrames.ReadLaunchAsync(stream, CancellationToken.None);
    Check(decoded == launch || decoded.ProfileName == launch.ProfileName && decoded.Arguments.SequenceEqual(launch.Arguments) &&
          decoded.Environment.SequenceEqual(launch.Environment), "launch frame did not round-trip the full contract");
}

await using (var oversized = new MemoryStream())
{
    await oversized.WriteAsync("AFW1"u8.ToArray());
    var size = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(size, TerminalLimits.PrivateFrameBytes + 1);
    await oversized.WriteAsync(size);
    oversized.Position = 0;
    await Rejects(() => WorkerFrames.ReadLaunchAsync(oversized, CancellationToken.None).AsTask(),
        "oversized private launch frame was accepted");
}

await using (var malformed = new MemoryStream())
{
    byte[] json = Encoding.UTF8.GetBytes("{\"ProfileName\":\"Command Prompt\",\"unexpected\":true}");
    await malformed.WriteAsync("AFW1"u8.ToArray());
    var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, json.Length);
    await malformed.WriteAsync(size); await malformed.WriteAsync(json); malformed.Position = 0;
    await Rejects(() => WorkerFrames.ReadLaunchAsync(malformed, CancellationToken.None).AsTask(),
        "partial or extension launch object was accepted");
}

await using (var controls = new MemoryStream())
{
    await WorkerFrames.WriteInputAsync(controls, Encoding.UTF8.GetBytes("hello"), CancellationToken.None);
    await WorkerFrames.WriteResizeAsync(controls, 120, 30, CancellationToken.None);
    controls.Position = 0;
    var first = await WorkerFrames.ReadControlAsync(controls, CancellationToken.None);
    var second = await WorkerFrames.ReadControlAsync(controls, CancellationToken.None);
    Check(first.Kind == WorkerControlKind.Input && Encoding.UTF8.GetString(first.Data) == "hello",
        "input frame lost bytes");
    Check(second.Kind == WorkerControlKind.Resize && second.Columns == 120 && second.Rows == 30,
        "resize frame lost dimensions");
}

await Rejects(async () =>
{
    await using var stream = new MemoryStream();
    await WorkerFrames.WriteInputAsync(stream, new byte[TerminalLimits.InputBytes + 1], CancellationToken.None);
}, "oversized input reached private IPC");

var generations = new AttachmentGeneration();
var firstGeneration = Guid.NewGuid().ToString();
var secondGeneration = Guid.NewGuid().ToString();
Check(generations.TryClaim(firstGeneration, out var firstLease), "first attachment was rejected");
Check(generations.TryClaim(secondGeneration, out var secondLease), "replacement attachment was rejected");
Check(firstLease.IsCancelled && !secondLease.IsCancelled && !generations.IsCurrent(firstGeneration) &&
      generations.IsCurrent(secondGeneration), "replacement did not atomically revoke the stale generation");
generations.Revoke();
Check(secondLease.IsCancelled && !generations.TryClaim(Guid.NewGuid().ToString(), out _),
    "quiesce did not revoke and reject attachments");
firstLease.Dispose();
secondLease.Dispose();

await TransportChecksAsync().WaitAsync(TimeSpan.FromSeconds(15));

Console.WriteLine("Terminal service offline checks passed: bounds, replay, rate gate, strict private frames, and attachment race semantics. No native process was launched.");
}
catch (Exception error)
{
    Console.Error.WriteLine("TERMINAL_CHECK_FAILED: " + error);
    Environment.ExitCode = 1;
}
