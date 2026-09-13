using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AgentFoundry.Desktop;

static class Program
{
    static readonly string SelfExecutable = Environment.ProcessPath
        ?? throw new InvalidOperationException("The .NET host path is unavailable.");
    static readonly string AssemblyPath = Assembly.GetExecutingAssembly().Location;
    static readonly bool HostedByDotnet = string.Equals(
        Path.GetFileNameWithoutExtension(SelfExecutable), "dotnet", StringComparison.OrdinalIgnoreCase);

    static async Task<int> Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
            SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);

        if (args is ["--check-pty"])
        {
            Console.WriteLine($"RUNNER_PID:{Environment.ProcessId}");
            string ptyRoot = Directory.CreateTempSubdirectory("agent-foundry-native-").FullName;
            try
            {
                await CheckPtyWorker(ptyRoot);
                Console.WriteLine("PASS: ConPTY checks");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                Directory.Delete(ptyRoot, recursive: true);
            }
        }

        if (args.Length > 0)
        {
            try
            {
                return await RunChild(args[0]);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: Windows-only native checks.");
            return 0;
        }

        Console.WriteLine($"RUNNER_PID:{Environment.ProcessId}");
        string testRoot = Directory.CreateTempSubdirectory("agent-foundry-native-").FullName;
        try
        {
            await CheckCreationTimeOwnership(testRoot);
            await CheckDescendantCleanup(testRoot);
            await CheckBlockedInputDispose(testRoot);
            await CheckOwnerCrashCleanup(testRoot);
            await CheckPtyWorker(testRoot);
            Console.WriteLine("PASS: native Job and ConPTY checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    static async Task<int> RunChild(string command)
    {
        switch (command)
        {
            case "wait":
                Console.WriteLine(Environment.ProcessId);
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "descendant":
                using (Process child = StartSelf("wait", redirectOutput: true))
                {
                    Console.WriteLine(await child.StandardOutput.ReadLineAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5)));
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                return 0;
            case "owner-crash":
                using (OwnedProcess child = StartOwned("wait", Environment.CurrentDirectory))
                using (var reader = new StreamReader(child.StandardOutput, leaveOpen: true))
                {
                    Console.WriteLine(await reader.ReadLineAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5)));
                    Console.Out.Flush();
                    Environment.Exit(86);
                }
                return 86;
            case "pty-child":
                Console.WriteLine("READY");
                string? input = Console.ReadLine();
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(2)
                       && (Console.WindowWidth != 100 || Console.WindowHeight != 40))
                    await Task.Delay(20);
                Console.WriteLine($"SIZE:{Console.WindowWidth}x{Console.WindowHeight}");
                Console.WriteLine("ECHO:" + input);
                string line = new('x', 256);
                for (int i = 0; i < 5000; i++)
                    Console.WriteLine(line);
                Console.WriteLine("DONE");
                return 7;
            case "pty-flood":
                Console.WriteLine("FLOOD_READY");
                string flood = new('y', 4096);
                while (true)
                    Console.WriteLine(flood);
            case "pty-worker":
                return await RunPtyChecks();
            default:
                throw new ArgumentException("Unknown child command.", nameof(command));
        }
    }

    static async Task CheckCreationTimeOwnership(string cwd)
    {
        using var lifetime = new SafeJob();
        await using OwnedProcess first = StartOwned("wait", cwd, lifetime);
        await using OwnedProcess second = StartOwned("wait", cwd, lifetime);
        using var firstReader = new StreamReader(first.StandardOutput, leaveOpen: true);
        using var secondReader = new StreamReader(second.StandardOutput, leaveOpen: true);
        int firstPid = int.Parse((await firstReader.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)))!);
        int secondPid = int.Parse((await secondReader.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)))!);
        Assert(firstPid == first.ProcessId && secondPid == second.ProcessId && firstPid != secondPid,
               "child PIDs should match distinct native process information");
        using Process firstProcess = Process.GetProcessById(firstPid);
        using Process secondProcess = Process.GetProcessById(secondPid);
        Assert(lifetime.Owns(firstProcess) && lifetime.Owns(secondProcess),
               "both children must join the shared lifetime Job at creation");
        Assert(first.OwnsForCheck(firstProcess) && second.OwnsForCheck(secondProcess),
               "each child must join its distinct pane Job at creation");
        Assert(!first.OwnsForCheck(secondProcess) && !second.OwnsForCheck(firstProcess),
               "pane Jobs must remain distinct under the shared lifetime Job");
        IReadOnlyList<int> assigned = lifetime.AssociatedProcessIds;
        Assert(assigned.Contains(firstPid) && assigned.Contains(secondPid),
               "lifetime Job inventory must include both live children");
        first.Stop();
        second.Stop();
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!IsAlive(firstPid) && !IsAlive(secondPid), "creation-time children survived tree stop");
        Assert(lifetime.AssociatedProcessIds.Count == 0,
               "lifetime Job inventory must clear after both process handles signal exit");
        Console.WriteLine($"CLEAN:creation:{firstPid},{secondPid}");
    }

    static async Task CheckDescendantCleanup(string cwd)
    {
        await using OwnedProcess owner = StartOwned("descendant", cwd);
        using var reader = new StreamReader(owner.StandardOutput, leaveOpen: true);
        int descendantPid = int.Parse((await reader.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)))!);
        Assert(IsAlive(descendantPid), "descendant should be running before tree stop");
        owner.Stop();
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForGone(descendantPid);
        Console.WriteLine($"CLEAN:descendant:{owner.ProcessId},{descendantPid}");
    }

    static async Task CheckOwnerCrashCleanup(string cwd)
    {
        using Process owner = StartSelf("owner-crash", redirectOutput: true, cwd);
        int childPid = int.Parse((await owner.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)))!);
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(owner.ExitCode != 0, "crash-owner helper must fail intentionally");
        await WaitForGone(childPid);
        Console.WriteLine($"CLEAN:owner-exit:{owner.Id},{childPid}");
    }

    static async Task CheckBlockedInputDispose(string cwd)
    {
        var child = StartOwned("wait", cwd);
        try
        {
            using var reader = new StreamReader(child.StandardOutput, leaveOpen: true);
            int childPid = int.Parse((await reader.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5)))!);
            Task write = child.StandardInput.WriteAsync(new byte[1_000_000]).AsTask();
            Assert(await Task.WhenAny(write, Task.Delay(100)) != write,
                   "blocked-input check did not fill the child pipe");
            await Task.Run(child.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
            try { await write; } catch (IOException) { } catch (ObjectDisposedException) { }
            await WaitForGone(childPid);
            Console.WriteLine($"CLEAN:blocked-input:{childPid}");
        }
        finally
        {
            child.Dispose();
        }
    }

    static async Task CheckPtyWorker(string cwd)
    {
        await using OwnedProcess worker = StartOwned("pty-worker", cwd);
        Console.WriteLine($"PTY_WORKER_PID:{worker.ProcessId}");
        using var reader = new StreamReader(worker.StandardOutput, leaveOpen: true);
        string output = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
        int exit = await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert(exit == 0, "Job-contained ConPTY worker failed: " + output);
        Assert(output.Contains("PTY_OK", StringComparison.Ordinal), "ConPTY worker did not finish its checks");
        Console.Write(output);
        Assert(!IsAlive(worker.ProcessId), "ConPTY worker survived completion");
        Console.WriteLine($"CLEAN:pty-worker:{worker.ProcessId}");
    }

    static async Task<int> RunPtyChecks()
    {
        try
        {
            AssertThrows<ArgumentOutOfRangeException>(() =>
                PtySession.Start(SelfExecutable, SelfArguments("pty-child"), Environment.CurrentDirectory,
                                 ChildEnvironment(), 1, 25));
            AssertThrows<ArgumentOutOfRangeException>(() =>
                PtySession.Start(SelfExecutable, SelfArguments("pty-child"), Environment.CurrentDirectory,
                                 ChildEnvironment(), 80, 301));

            await using (PtySession session = PtySession.Start(
                SelfExecutable, SelfArguments("pty-child"), Environment.CurrentDirectory,
                ChildEnvironment(), 80, 25))
            {
                using var reader = new StreamReader(session.Output, Encoding.UTF8, leaveOpen: true);
                string? ready = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert(ready?.Contains("READY", StringComparison.Ordinal) == true,
                       "ConPTY child did not become ready");
                session.Resize(100, 40);
                await session.Input.WriteAsync(Encoding.UTF8.GetBytes("hello\r\n"));
                await session.Input.FlushAsync();
                var captured = new StringBuilder().AppendLine(ready);
                for (int lineNumber = 0; lineNumber < 6_000; lineNumber++)
                {
                    string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if (line is null)
                        throw new InvalidOperationException("ConPTY output ended before DONE");
                    captured.AppendLine(line);
                    if (line.Contains("DONE", StringComparison.Ordinal))
                        break;
                }
                string remaining = captured.ToString();
                Task<string> remainder = reader.ReadToEndAsync();
                int exit;
                try
                {
                    exit = await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    Console.WriteLine($"PTY_EXIT_TIMEOUT:{session.ProcessId}:hasExited={session.HasExited}:exit={session.ExitCode}");
                    await session.CloseAsync(remainder);
                    throw;
                }
                await session.CloseAsync(remainder);
                remaining += await remainder;
                Assert(exit == 7, "ConPTY exit code was not preserved");
                Assert(remaining.Contains("READY", StringComparison.Ordinal), "ConPTY ready output was lost");
                Assert(remaining.Contains("SIZE:100x40", StringComparison.Ordinal), "ConPTY resize was not observed");
                Assert(remaining.Contains("ECHO:hello", StringComparison.Ordinal), "ConPTY input/output failed");
                Assert(remaining.Contains("DONE", StringComparison.Ordinal) && remaining.Length > 1_000_000,
                       "ConPTY output flood was truncated or blocked");
                Console.WriteLine($"CLEAN:pty-io:{session.ProcessId}");
            }

            await using (PtySession flood = PtySession.Start(
                SelfExecutable, SelfArguments("pty-flood"), Environment.CurrentDirectory,
                ChildEnvironment(), 80, 25))
            {
                using var floodReader = new StreamReader(flood.Output, Encoding.UTF8, leaveOpen: true);
                string? ready = await floodReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert(ready?.Contains("FLOOD_READY", StringComparison.Ordinal) == true,
                       "ConPTY flood child did not become ready");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await flood.CloseAsync(timeout.Token);
                Assert(!IsAlive(flood.ProcessId), "ConPTY flood child survived close");
                Console.WriteLine($"CLEAN:pty-flood:{flood.ProcessId}");
            }

            Console.WriteLine("PTY_OK");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(error);
            return 1;
        }
    }

    static OwnedProcess StartOwned(string command, string cwd, SafeJob? lifetime = null) =>
        OwnedProcess.Start(SelfExecutable, SelfArguments(command), cwd, ChildEnvironment(), lifetime);

    static IReadOnlyList<string> SelfArguments(string command) =>
        HostedByDotnet ? [AssemblyPath, command] : [command];

    static Process StartSelf(string command, bool redirectOutput, string? cwd = null)
    {
        var start = new ProcessStartInfo(SelfExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        if (HostedByDotnet)
            start.ArgumentList.Add(AssemblyPath);
        start.ArgumentList.Add(command);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start test helper.");
    }

    static Dictionary<string, string> ChildEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "SystemRoot", "WINDIR", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                result[name] = value;
        result["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        result["TEMP"] = Path.GetTempPath();
        result["TMP"] = Path.GetTempPath();
        return result;
    }

    static async Task WaitForGone(int processId)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5) && IsAlive(processId))
            await Task.Delay(25);
        Assert(!IsAlive(processId), $"process {processId} survived owned Job cleanup");
    }

    static bool IsAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    const uint SemFailCriticalErrors = 0x0001;
    const uint SemNoGpFaultErrorBox = 0x0002;
    const uint SemNoOpenFileErrorBox = 0x8000;

    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);
}
