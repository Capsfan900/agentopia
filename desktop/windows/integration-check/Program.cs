using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentFoundry.Desktop;

static class Program
{
    static readonly string AssemblyPath = Assembly.GetExecutingAssembly().Location;

    static async Task<int> Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
            SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);

        if (args is ["--terminal-worker"])
            return await TerminalWorker.RunAsync();

        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows is required.");
            Metric("runtime", new
            {
                framework = RuntimeInformation.FrameworkDescription,
                environmentVersion = Environment.Version.ToString(),
                sdkPin = "8.0.401",
            });
            using var wholeRun = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            if (args is ["--one-pane", string onePaneHash])
                await RunScenarioAsync(1, exerciseReplay: true, onePaneHash, wholeRun.Token);
            else if (args is ["--all", string allHash])
            {
                await RunScenarioAsync(1, exerciseReplay: true, allHash, wholeRun.Token);
                await RunScenarioAsync(2, exerciseReplay: false, allHash, wholeRun.Token);
                await RunScenarioAsync(8, exerciseReplay: false, allHash, wholeRun.Token);
            }
            else
                throw new ArgumentException("Expected --one-pane SHA256 or --all SHA256.");
            Metric("complete", new { status = "pass" });
            return 0;
        }
        catch (Exception error)
        {
            Metric("error", new
            {
                status = "fail",
                type = error.GetType().Name,
                error.Message,
                error.StackTrace,
            });
            return 1;
        }
    }

    static async Task RunScenarioAsync(
        int paneCount, bool exerciseReplay, string reviewedHash, CancellationToken cancellationToken)
    {
        using var lifetimeJob = new SafeJob();
        string testRoot = Directory.CreateTempSubdirectory("foundry-terminal-integration-").FullName;
        TerminalServer? server = null;
        HeldResources? resources = null;
        var sockets = new List<ClientWebSocket>();
        try
        {
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            string worker = Environment.ProcessPath
                ?? throw new InvalidOperationException("The .NET host path is unavailable.");
            server = await TerminalServer.StartAsync(
                Path.Combine(AppContext.BaseDirectory, "terminal"), secret, worker,
                [AssemblyPath], lifetimeJob).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            using HttpClient client = CreateHttpClient(server.Origin);
            using (var assetRequest = new HttpRequestMessage(HttpMethod.Get, "/"))
            {
                assetRequest.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, secret);
                using HttpResponseMessage asset = await client.SendAsync(assetRequest, cancellationToken);
                Check(asset.StatusCode == HttpStatusCode.OK && asset.Content.Headers.ContentLength is > 0,
                    "verified terminal asset delivery failed");
            }
            TerminalLaunch launch = CreateLaunch(testRoot, reviewedHash);
            var paneIds = new List<string>(paneCount);
            var launchMilliseconds = new List<double>(paneCount);

            for (int index = 0; index < paneCount; index++)
            {
                var timer = Stopwatch.StartNew();
                paneIds.Add(await server.LaunchAsync(launch, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
                launchMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
            }

            var connections = new List<Connection>(paneCount);
            foreach (string paneId in paneIds)
            {
                Connection connection = await ConnectAsync(client, server.Origin, secret, paneId, cancellationToken);
                sockets.Add(connection.Socket);
                connections.Add(connection);
            }

            resources = await MeasureIdleAsync(
                lifetimeJob, server, paneIds, paneCount, cancellationToken);
            Metric("idle", new
            {
                panes = paneCount,
                launchMilliseconds,
                ownedProcessCount = resources.Identities.Count,
                ownedProcessIds = resources.Identities.Select(x => x.Id).ToArray(),
                ownedWorkingSetBytes = resources.WorkingSetBytes,
                idleMachineCpuPercent = resources.MachineCpuPercent,
                serviceHostWorkingSetBytes = resources.HostWorkingSetBytes,
                serviceHostMachineCpuPercent = resources.HostMachineCpuPercent,
                sampleSeconds = 5,
                guard = "coarse-runaway-only",
            });

            if (exerciseReplay)
            {
                await RejectReusedTicketAsync(server.Origin, connections[0], cancellationToken);
                await ExerciseReplayAndSlowClientAsync(
                    client, server.Origin, secret, paneIds[0], connections[0], sockets, cancellationToken);
                connections.Clear();
            }
            else
            {
                double[] echo = await Task.WhenAll(connections.Select((connection, index) =>
                    ExitNaturallyAsync(connection.Socket, index, cancellationToken)));
                Metric("echo", new { panes = paneCount, echoMilliseconds = echo });
            }

            foreach (string paneId in paneIds)
            {
                await WaitForExitCodeAsync(client, secret, paneId, 7, cancellationToken);
                await ClosePaneAsync(client, server.Origin, secret, paneId, cancellationToken);
            }

            foreach (ClientWebSocket socket in sockets)
                socket.Dispose();
            sockets.Clear();
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            server = null;
            await WaitForJobEmptyAsync(lifetimeJob, resources.Identities, cancellationToken);
            Metric("cleanup", new { panes = paneCount, associatedProcessCount = 0, status = "pass" });
        }
        finally
        {
            foreach (ClientWebSocket socket in sockets) socket.Dispose();
            if (server is not null)
            {
                try { await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { try { lifetimeJob.Terminate(); } catch { } }
            }
            resources?.Dispose();
            try { lifetimeJob.Terminate(); } catch { }
            Directory.Delete(testRoot, recursive: true);
        }
    }

    static async Task ExerciseReplayAndSlowClientAsync(
        HttpClient client, string origin, string secret, string paneId, Connection first,
        List<ClientWebSocket> sockets, CancellationToken cancellationToken)
    {
        const int payloadLength = 8_000;
        string payload = new('x', payloadLength);
        string floodMarker = "AF_FLOOD_" + Guid.NewGuid().ToString("N");
        Task<long> fastReceive = ReceiveUntilAsync(first.Socket, floodMarker, 16L * 1024 * 1024, cancellationToken);
        for (int index = 0; index < 132; index++)
        {
            await SendInputAsync(first.Socket, "echo " + payload + "\r\n", cancellationToken);
            await Task.Delay(12, cancellationToken);
        }
        await SendInputAsync(first.Socket, "echo " + floodMarker + "\r\n", cancellationToken);
        long floodBytes = await fastReceive.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Check(floodBytes > TerminalLimits.ReplayBytes, "flood output did not exceed the replay bound");
        await CloseSocketOutputAsync(first.Socket, cancellationToken);
        first.Socket.Dispose();
        sockets.Remove(first.Socket);

        Connection replay = await ConnectAsync(client, origin, secret, paneId, cancellationToken);
        sockets.Add(replay.Socket);
        Check(replay.Truncated, "replay was not marked truncated after bounded flood");
        long replayBytes = await ReceiveUntilAsync(
            replay.Socket, floodMarker, 2L * TerminalLimits.ReplayBytes, cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(1_100), cancellationToken);

        using Process current = Process.GetCurrentProcess();
        current.Refresh();
        long before = current.WorkingSet64;
        var slowTimer = Stopwatch.StartNew();
        using var slowDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        slowDeadline.CancelAfter(TimeSpan.FromSeconds(12));
        CancellationToken slowToken = slowDeadline.Token;
        int minimumBacklogFrames = TerminalLimits.AttachmentBacklogBytes / payloadLength + 1;
        int accepted = 0;
        bool boundedTransportAbort = false;
        for (int index = 0; index < 512; index++)
        {
            try
            {
                await SendInputAsync(replay.Socket, "echo " + payload + "\r\n", slowToken);
                accepted++;
                await Task.Delay(12, slowToken);
            }
            catch (WebSocketException error) when (
                error.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                boundedTransportAbort = true;
                break;
            }
        }
        WebSocketCloseStatus? status = null;
        string? reason = null;
        long discarded = 0;
        if (!boundedTransportAbort)
        {
            try
            {
                (status, reason, discarded) =
                    await ReceiveCloseAsync(replay.Socket, 32L * 1024 * 1024, slowToken);
            }
            catch (WebSocketException error) when (
                error.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                boundedTransportAbort = true;
            }
        }
        slowTimer.Stop();
        current.Refresh();
        long growth = current.WorkingSet64 - before;
        Check(accepted >= minimumBacklogFrames,
            "slow-client check did not send enough output to exercise the backlog bound");
        Check(boundedTransportAbort ||
              status == WebSocketCloseStatus.PolicyViolation && reason == "Terminal client too slow",
            "slow terminal client produced neither an exact policy close nor a bounded transport abort");
        Check(slowTimer.Elapsed <= TimeSpan.FromSeconds(12),
            "slow-client phase exceeded its 12 second bound");
        Check(growth < 512L * 1024 * 1024,
            "slow-client working-set growth tripped the coarse 512 MiB runaway guard");
        replay.Socket.Dispose();
        sockets.Remove(replay.Socket);

        Connection exit = await ConnectAsync(client, origin, secret, paneId, cancellationToken);
        sockets.Add(exit.Socket);
        double exitEcho = await ExitNaturallyAsync(exit.Socket, 0, cancellationToken);
        Metric("replay", new
        {
            panes = 1,
            floodBytes,
            replayBytes,
            truncated = replay.Truncated,
            slowFramesAccepted = accepted,
            minimumBacklogFrames,
            slowBytesDiscarded = discarded,
            slowWorkingSetGrowthBytes = growth,
            slowCloseOutcome = boundedTransportAbort ? "bounded-transport-abort" : "policy-1008",
            slowMilliseconds = slowTimer.Elapsed.TotalMilliseconds,
            exitEchoMilliseconds = exitEcho,
        });
    }

    static async Task<double> ExitNaturallyAsync(
        ClientWebSocket socket, int index, CancellationToken cancellationToken)
    {
        string marker = $"AF_EXIT_{index}_" + Guid.NewGuid().ToString("N");
        await SendResizeAsync(socket, 100, 40, cancellationToken);
        var timer = Stopwatch.StartNew();
        Task<long> receive = ReceiveUntilAsync(socket, marker, 32L * 1024 * 1024, cancellationToken);
        await SendInputAsync(socket, "echo " + marker + " & exit /b 7\r\n", cancellationToken);
        await receive.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        return timer.Elapsed.TotalMilliseconds;
    }

    static TerminalLaunch CreateLaunch(string workingDirectory, string reviewedHash)
    {
        if (reviewedHash.Length != 64 || reviewedHash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("Reviewed cmd.exe SHA256 must be lowercase hexadecimal.");
        string executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        string[] allowed = ["SystemRoot", "WINDIR", "TEMP", "TMP"];
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in allowed)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                environment.Add(name, value);
        environment["TERM"] = "xterm-256color";
        return new("Command Prompt", executable, reviewedHash, ["/d"], workingDirectory,
            environment, "new", null, null);
    }

    static HttpClient CreateHttpClient(string origin)
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        return new HttpClient(handler) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(5) };
    }

    static async Task<Connection> ConnectAsync(
        HttpClient client, string origin, string secret, string paneId, CancellationToken cancellationToken)
    {
        string generation = Guid.NewGuid().ToString();
        string ticket = await MintTicketAsync(client, origin, secret, paneId, generation, cancellationToken);
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            using var deadline = LinkedDeadline(cancellationToken);
            await socket.ConnectAsync(WebSocketUri(origin), deadline.Token);
            await SendJsonAsync(socket, new { paneId, generation, ticket }, deadline.Token);
            byte[] ready = await ReceiveTextAsync(socket, TerminalLimits.PrivateFrameBytes, deadline.Token);
            using JsonDocument document = JsonDocument.Parse(ready);
            Check(document.RootElement.GetProperty("kind").GetString() == "ready", "ready frame was absent");
            bool truncated = document.RootElement.GetProperty("truncated").GetBoolean();
            return new(socket, paneId, generation, ticket, truncated);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    static async Task<string> MintTicketAsync(
        HttpClient client, string origin, string secret, string paneId, string generation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ticket");
        request.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, secret);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Content = JsonContent.Create(new { paneId, generation });
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        Check(response.StatusCode == HttpStatusCode.OK, "ticket request failed");
        using JsonDocument payload = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(cancellationToken));
        return payload.RootElement.GetProperty("ticket").GetString()
            ?? throw new InvalidDataException("ticket was absent");
    }

    static async Task RejectReusedTicketAsync(
        string origin, Connection connection, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.SetRequestHeader("Origin", origin);
        using var deadline = LinkedDeadline(cancellationToken);
        await socket.ConnectAsync(WebSocketUri(origin), deadline.Token);
        await SendJsonAsync(socket, new
        {
            paneId = connection.PaneId,
            generation = connection.Generation,
            ticket = connection.Ticket,
        }, deadline.Token);
        byte[] buffer = new byte[256];
        WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
        Check(result.MessageType == WebSocketMessageType.Close &&
              result.CloseStatus == WebSocketCloseStatus.PolicyViolation,
            "one-use attachment ticket was accepted twice");
    }

    static async Task WaitForExitCodeAsync(
        HttpClient client, string secret, string paneId, int expected, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/panes");
            request.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, secret);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            Check(response.StatusCode == HttpStatusCode.OK, "pane snapshot request failed");
            using JsonDocument payload = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(cancellationToken));
            JsonElement pane = payload.RootElement.GetProperty("panes").EnumerateArray()
                .Single(x => x.GetProperty("id").GetString() == paneId);
            if (pane.GetProperty("status").GetString() == "exited")
            {
                Check(pane.GetProperty("exitCode").GetInt32() == expected,
                    "natural terminal exit code was not preserved");
                return;
            }
            await Task.Delay(25, cancellationToken);
        }
        throw new TimeoutException("pane did not report natural exit");
    }

    static async Task ClosePaneAsync(
        HttpClient client, string origin, string secret, string paneId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/panes/{paneId}/close");
        request.Headers.TryAddWithoutValidation(TerminalServer.AuthorizationHeader, secret);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Content = JsonContent.Create(new { confirmed = true });
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        Check(response.StatusCode == HttpStatusCode.NoContent, "confirmed pane close failed");
    }

    static async Task<HeldResources> MeasureIdleAsync(
        SafeJob job, TerminalServer server, IReadOnlyList<string> paneIds, int panes,
        CancellationToken cancellationToken)
    {
        List<ProcessIdentity> startup = CaptureIdentities(job);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            PaneInfo[] states = server.Panes.Where(x => paneIds.Contains(x.Id)).ToArray();
            ProcessIdentity[] disappeared = startup.Where(x => x.Process.HasExited).ToArray();
            if (states.Length != panes || states.Any(x => x.Status != "running") || disappeared.Length > 0)
            {
                string paneState = JsonSerializer.Serialize(states.Select(x => new
                    { x.Id, x.ProfileName, x.Status, x.ExitCode }));
                string processState = JsonSerializer.Serialize(disappeared.Select(ProcessStatus));
                throw new InvalidOperationException(
                    $"pane or owned process exited during settle; panes={paneState}; processes={processState}");
            }
        }
        finally
        {
            foreach (ProcessIdentity identity in startup) identity.Dispose();
        }

        List<ProcessIdentity> identities = CaptureIdentities(job);
        Check(identities.Count >= panes * 2, "owned Job inventory omitted worker or shell processes");
        try
        {
            using Process host = Process.GetCurrentProcess();
            TimeSpan cpuBefore = TimeSpan.FromTicks(identities.Sum(x => x.Process.TotalProcessorTime.Ticks));
            long workingSet = identities.Sum(x => { x.Process.Refresh(); return x.Process.WorkingSet64; });
            host.Refresh();
            TimeSpan hostCpuBefore = host.TotalProcessorTime;
            long hostWorkingSet = host.WorkingSet64;
            var timer = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            ProcessIdentity[] disappeared = identities.Where(x => x.Process.HasExited).ToArray();
            if (disappeared.Length > 0)
                throw new InvalidOperationException("owned process exited during idle sample: " +
                    JsonSerializer.Serialize(disappeared.Select(ProcessStatus)));
            TimeSpan cpuAfter = TimeSpan.FromTicks(identities.Sum(x => x.Process.TotalProcessorTime.Ticks));
            host.Refresh();
            double machineCpu = (cpuAfter - cpuBefore).TotalMilliseconds /
                timer.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            double hostMachineCpu = (host.TotalProcessorTime - hostCpuBefore).TotalMilliseconds /
                timer.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            Check(machineCpu < 50 && hostMachineCpu < 50,
                "idle CPU tripped the coarse 50 percent runaway guard");
            return new(identities, workingSet, machineCpu, hostWorkingSet, hostMachineCpu);
        }
        catch
        {
            foreach (ProcessIdentity identity in identities) identity.Dispose();
            throw;
        }
    }

    static List<ProcessIdentity> CaptureIdentities(SafeJob job)
    {
        IReadOnlyList<int> ids = job.AssociatedProcessIds;
        var result = new List<ProcessIdentity>(ids.Count);
        try
        {
            foreach (int id in ids)
            {
                Process process = Process.GetProcessById(id);
                if (!job.Owns(process))
                {
                    process.Dispose();
                    throw new InvalidOperationException($"Job inventory returned foreign PID {id}");
                }
                try
                {
                    result.Add(new(process, process.ProcessName, process.StartTime.ToUniversalTime()));
                }
                catch
                {
                    bool exited = process.HasExited;
                    int? exitCode = exited ? process.ExitCode : null;
                    process.Dispose();
                    throw new InvalidOperationException(
                        $"owned PID {id} became unavailable during identity capture; " +
                        $"hasExited={exited}; exitCode={exitCode}");
                }
            }
            return result;
        }
        catch
        {
            foreach (ProcessIdentity identity in result) identity.Dispose();
            throw;
        }
    }

    static object ProcessStatus(ProcessIdentity identity)
    {
        bool exited = identity.Process.HasExited;
        return new
        {
            identity.Id,
            identity.Name,
            identity.StartedAtUtc,
            hasExited = exited,
            exitCode = exited ? (int?)identity.Process.ExitCode : null,
        };
    }

    static async Task WaitForJobEmptyAsync(
        SafeJob job, IReadOnlyList<ProcessIdentity> identities, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (job.AssociatedProcessIds.Count == 0 && identities.All(x => x.Process.HasExited)) return;
            await Task.Delay(25, cancellationToken);
        }
        throw new TimeoutException("owned Job did not become empty after cleanup");
    }

    static async Task SendInputAsync(
        ClientWebSocket socket, string data, CancellationToken cancellationToken)
    {
        using var deadline = LinkedDeadline(cancellationToken);
        await SendJsonAsync(socket, new { kind = "input", data }, deadline.Token);
    }

    static async Task SendResizeAsync(
        ClientWebSocket socket, int columns, int rows, CancellationToken cancellationToken)
    {
        using var deadline = LinkedDeadline(cancellationToken);
        await SendJsonAsync(socket, new { kind = "resize", columns, rows }, deadline.Token);
    }

    static async Task SendJsonAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken);
    }

    static async Task<byte[]> ReceiveTextAsync(
        ClientWebSocket socket, int maximum, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            using var deadline = LinkedDeadline(cancellationToken);
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), deadline.Token);
            Check(result.MessageType == WebSocketMessageType.Text, "expected text WebSocket frame");
            Check(content.Length + result.Count <= maximum, "WebSocket text frame exceeded bound");
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return content.ToArray();
        }
    }

    static async Task<long> ReceiveUntilAsync(
        ClientWebSocket socket, string marker, long maximum, CancellationToken cancellationToken)
    {
        var scanner = new MarkerScanner(Encoding.ASCII.GetBytes(marker));
        byte[] buffer = new byte[TerminalLimits.OutputChunkBytes];
        long received = 0;
        while (received <= maximum)
        {
            using var deadline = LinkedDeadline(cancellationToken);
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), deadline.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("terminal WebSocket closed before expected marker");
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new InvalidDataException("unexpected terminal WebSocket message type");
            received += result.Count;
            if (scanner.Add(buffer.AsSpan(0, result.Count))) return received;
        }
        throw new InvalidDataException("terminal output exceeded checker bound before marker");
    }

    static async Task<(WebSocketCloseStatus? Status, string? Reason, long Discarded)> ReceiveCloseAsync(
        ClientWebSocket socket, long maximum, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[TerminalLimits.OutputChunkBytes];
        long received = 0;
        while (received <= maximum)
        {
            using var deadline = LinkedDeadline(cancellationToken);
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), deadline.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return (result.CloseStatus, result.CloseStatusDescription, received);
            received += result.Count;
        }
        throw new InvalidDataException("slow-client output exceeded checker receive bound");
    }

    static async Task CloseSocketOutputAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var deadline = LinkedDeadline(cancellationToken);
        if (socket.State == WebSocketState.Open)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "checker detach", deadline.Token);
    }

    static Uri WebSocketUri(string origin) => new("ws" + origin[4..] + "/connect");

    static CancellationTokenSource LinkedDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return deadline;
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Metric(string kind, object value) =>
        Console.WriteLine(JsonSerializer.Serialize(new { kind, value }));

    sealed record Connection(
        ClientWebSocket Socket, string PaneId, string Generation, string Ticket, bool Truncated);

    sealed class ProcessIdentity(Process process, string name, DateTime startedAtUtc) : IDisposable
    {
        public Process Process { get; } = process;
        public int Id => Process.Id;
        public string Name { get; } = name;
        public DateTime StartedAtUtc { get; } = startedAtUtc;
        public void Dispose() => Process.Dispose();
    }

    sealed class HeldResources(
        List<ProcessIdentity> identities, long workingSetBytes, double machineCpuPercent,
        long hostWorkingSetBytes, double hostMachineCpuPercent) : IDisposable
    {
        public List<ProcessIdentity> Identities { get; } = identities;
        public long WorkingSetBytes { get; } = workingSetBytes;
        public double MachineCpuPercent { get; } = machineCpuPercent;
        public long HostWorkingSetBytes { get; } = hostWorkingSetBytes;
        public double HostMachineCpuPercent { get; } = hostMachineCpuPercent;
        public void Dispose() { foreach (ProcessIdentity identity in Identities) identity.Dispose(); }
    }

    sealed class MarkerScanner(byte[] pattern)
    {
        int matched;
        public bool Add(ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
            {
                if (value == pattern[matched]) matched++;
                else matched = value == pattern[0] ? 1 : 0;
                if (matched == pattern.Length) return true;
            }
            return false;
        }
    }

    const uint SemFailCriticalErrors = 0x0001;
    const uint SemNoGpFaultErrorBox = 0x0002;
    const uint SemNoOpenFileErrorBox = 0x8000;

    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);
}
