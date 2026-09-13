using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgentFoundry.Desktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WebViewCheck;

static partial class Program
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    const int GwlExStyle = -20;
    const long WsExNoActivate = 0x08000000;

    [STAThread]
    public static int Main(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        if (!SetDefaultDllDirectories(0x1000)) return 1;
        DesktopProgram.ClearWebViewEnvironment();
        if (args.SequenceEqual(new[] { "--check-visible-fixture" }))
        {
            try { CheckVisibleFixture(); CheckVisibleCleanupAsync().GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args.SequenceEqual(new[] { "--check-guards" }))
        {
            try { CheckForegroundGuard(); CheckPerformanceGate(); CheckNavigationJournal(); CheckWebViewEnvironment(); CheckTerminalContextBridge(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exitCode = 1;
        Exception? dispatcherFailure = null;
        application.DispatcherUnhandledException += (_, e) =>
        {
            Interlocked.CompareExchange(ref dispatcherFailure, e.Exception, null);
            e.Handled = true;
        };
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            Exception? runFailure = null;
            try
            {
                if (args.SequenceEqual(new[] { "--visible-observatory" })) await RunVisibleAsync();
                else if (args.Length == 0) await RunAsync();
                else throw new ArgumentException("Unknown checker mode");
            }
            catch (Exception error) { runFailure = error; }
            await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Exception? asynchronousFailure = Volatile.Read(ref dispatcherFailure);
            Exception? failure = asynchronousFailure is null ? runFailure : runFailure is null
                ? asynchronousFailure : new AggregateException(runFailure, asynchronousFailure);
            if (failure is null) exitCode = 0;
            else Console.Error.WriteLine("WEBVIEW_CHECK_FAILED: " + failure);
            application.Shutdown(exitCode);
        }));
        application.Run();
        return exitCode;
    }

    static void CheckTerminalContextBridge()
    {
        string session = Guid.NewGuid().ToString(), pane = Guid.NewGuid().ToString();
        string source = @"C:\Users\fixture\.codex", cwd = @"C:\work\project";
        string snapshot = JsonSerializer.Serialize(new
        {
            adapter = "codex", source,
            sessions = new object[] { new
            {
                id = session, cwd, status = "working", turn_status = "inProgress", stale = false,
                approval_pending = true, updated = "2026-09-13T12:00:00-04:00",
                current_action = "raw command must not win",
                working_on = new { summary = "Implement safe parser", source = "agent_message", status = "approval" },
                saved_handoff = new { state = "saved", item_id = "work-1", revision = new string('a', 64),
                    captured_at = "2026-09-13T11:00:00-04:00", archived = false,
                    completion = new { completed = true, source = "imported", trusted = false } },
                prompt_context = new { text = "PRIVATE_PROMPT_MARKER" }, trace = new { detail = "PRIVATE_TRACE_MARKER" }
            } }
        });
        var launch = new TerminalLaunch("Codex", @"C:\fixture\codex.exe", new string('0', 64),
            [], cwd, new() { ["CODEX_HOME"] = source }, "resume", session, session);
        using var payload = JsonDocument.Parse(TerminalContextBridge.Project(snapshot,
            new Dictionary<string, TerminalLaunch> { [pane] = launch }));
        JsonElement root = payload.RootElement, context = root.GetProperty("panes")[0];
        Check(root.GetProperty("kind").GetString() == "sessionContext" &&
            context.GetProperty("paneId").GetString() == pane && context.GetProperty("relation").GetString() == "current" &&
            context.GetProperty("workingOn").GetString() == "Implement safe parser" &&
            context.GetProperty("approvalPending").GetBoolean() &&
            context.GetProperty("handoff").GetProperty("state").GetString() == "saved" &&
            !context.GetProperty("handoff").GetProperty("completion").GetProperty("trusted").GetBoolean(),
            "Terminal context did not preserve the narrow verified session projection");
        Check(!payload.RootElement.GetRawText().Contains("PRIVATE_", StringComparison.Ordinal) &&
            !payload.RootElement.GetRawText().Contains("raw command must not win", StringComparison.Ordinal),
            "Terminal context leaked prompt/trace/raw fallback data");
        Check(TerminalContextBridge.ExtractStateData("event: state") is null &&
            TerminalContextBridge.ExtractStateData("data: " + snapshot) == snapshot,
            "Terminal SSE parser accepted a non-data line or changed the bounded state payload");
        try { TerminalContextBridge.ExtractStateData("data: " + new string('x', 4 * 1024 * 1024 + 1)); throw new Exception("Oversized Terminal context accepted"); }
        catch (InvalidDataException) { }
        {
            var lines = new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes("data: one\ndata: two\n")), 32);
            Check(lines.ReadLineAsync(CancellationToken.None).Result == "data: one" &&
                lines.ReadLineAsync(CancellationToken.None).Result == "data: two" &&
                lines.ReadLineAsync(CancellationToken.None).Result is null,
                "bounded SSE reader did not preserve complete lines");
        }
        {
            var oversized = new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 33) + "\n")), 32);
            try { _ = oversized.ReadLineAsync(CancellationToken.None).Result; throw new Exception("Oversized SSE line accepted"); }
            catch (AggregateException error) when (error.InnerException is InvalidDataException) { }
        }
        {
            var truncated = new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes("data: partial")), 32);
            try { _ = truncated.ReadLineAsync(CancellationToken.None).Result; throw new Exception("Unterminated SSE line accepted"); }
            catch (AggregateException error) when (error.InnerException is EndOfStreamException) { }
        }

        using var fork = JsonDocument.Parse(TerminalContextBridge.Project(snapshot,
            new Dictionary<string, TerminalLaunch> { [pane] = launch with { Mode = "fork", SessionId = null } }));
        Check(fork.RootElement.GetProperty("panes")[0].GetProperty("relation").GetString() == "source",
            "Fork context was not explicitly labeled as source-only");
        using var mismatched = JsonDocument.Parse(TerminalContextBridge.Project(snapshot,
            new Dictionary<string, TerminalLaunch> { [pane] = launch with { Environment = new() { ["CODEX_HOME"] = @"C:\other" } } }));
        Check(mismatched.RootElement.GetProperty("panes")[0].GetProperty("relation").GetString() == "unavailable",
            "Mismatched provider source was treated as verified context");
        using var shell = JsonDocument.Parse(TerminalContextBridge.Project(snapshot,
            new Dictionary<string, TerminalLaunch> { [pane] = launch with { ProfileName = "Command Prompt", Mode = "new", SourceSessionId = null, SessionId = null } }));
        Check(shell.RootElement.GetProperty("panes")[0].GetProperty("relation").GetString() == "unlinked",
            "Ordinary shell was presented as a linked agent session");
        Console.Error.WriteLine("TERMINAL_CONTEXT_GUARD: exact current/source/unavailable/unlinked projection, bounded SSE and privacy checks passed");
    }

    static async Task RunAsync()
    {
        CheckForegroundGuard();
        CheckPerformanceGate();
        CheckOffDesktopGeometry();
        CheckNavigationJournal();
        CheckWebViewEnvironment();
        var stage = new StageTracker();
        string fixture = Path.Combine(Path.GetTempPath(), "agent-foundry-webview-check-" + Guid.NewGuid().ToString("N"));
        string cache = WebViewCacheFor(fixture);
        DesktopWindow? window = null;
        var held = new Dictionary<int, HeldProcess>();
        using var host = new HeldProcess(Environment.ProcessId);
        host.Roles.Add("checker-host");
        bool started = false;
        bool fixtureOwned = false, cacheOwned = false;
        try
        {
            Check(!Directory.Exists(fixture) && !Directory.Exists(cache),
                "generated fixture or WebView cache already existed");
            fixtureOwned = cacheOwned = true;
            WriteFixture(fixture);
            stage.Set("fixture-written");
            window = new DesktopWindow(fixture, enableTerminal: true);
            window.ShowActivated = false; window.ShowInTaskbar = false; window.Opacity = 0;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = SystemParameters.VirtualScreenLeft - window.Width - 1000;
            window.Top = SystemParameters.VirtualScreenTop;
            WebView2 dashboard = Field<WebView2>(window, "dashboard");
            WebView2 terminal = Field<WebView2>(window, "terminalView");
            var dashboardNavigation = new NavigationJournal();
            var terminalNavigation = new NavigationJournal();
            var status = Field<System.Windows.Controls.TextBlock>(window, "status");
            int loadedCount = 0;
            bool activated = false;
            window.Activated += (_, _) =>
            {
                if (!activated) Console.Error.WriteLine("WEBVIEW_ACTIVATED: " + JsonSerializer.Serialize(new
                    { foreground_hwnd = GetForegroundWindow().ToInt64(), active_hwnd = GetActiveWindow().ToInt64() }));
                activated = true;
            };
            window.Loaded += (_, _) => { loadedCount++; stage.Set("window-loaded-event"); };
            int navigationEvents = 0;
            void NavigationLog(object value)
            {
                if (navigationEvents++ < 32) Console.Error.WriteLine("WEBVIEW_NAV: " + JsonSerializer.Serialize(value));
            }
            foreach (var (view, name, journal) in new[]
                { (dashboard, "dashboard", dashboardNavigation), (terminal, "terminal", terminalNavigation) })
            view.CoreWebView2InitializationCompleted += (_, e) =>
            {
                stage.Set(name + (e.IsSuccess ? "-core-initialized" : "-core-initialization-failed"));
                if (!e.IsSuccess) return;
                var core = view.CoreWebView2;
                core.NavigationStarting += (_, n) =>
                {
                    NavigationLog(new { view = name, kind = "starting", id = n.NavigationId, uri = n.Uri, canceled = n.Cancel, redirect = n.IsRedirected });
                    journal.Starting(n.Uri, n.NavigationId);
                };
                core.SourceChanged += (_, n) => NavigationLog(new
                    { kind = "source-changed", uri = core.Source, new_document = n.IsNewDocument });
                core.DOMContentLoaded += (_, n) => NavigationLog(new { kind = "dom-content-loaded", id = n.NavigationId });
                core.NavigationCompleted += (_, n) =>
                {
                    NavigationLog(new { kind = "completed", id = n.NavigationId, success = n.IsSuccess, error = n.WebErrorStatus.ToString(), http_status = n.HttpStatusCode });
                    journal.Completed(n.NavigationId, n.IsSuccess);
                };
            };
            nint hwnd = new WindowInteropHelper(window).EnsureHandle();
            Check(hwnd != 0 && !window.IsVisible && !IsWindowVisible(hwnd),
                "test window became visible before startup");
            nint expectedForeground = GetForegroundWindow();
            Check(expectedForeground != 0 && expectedForeground != hwnd, "user foreground window was unavailable before test startup");
            nint extendedStyle = GetWindowLongPtrW(hwnd, GwlExStyle);
            Check(extendedStyle != 0 || Marshal.GetLastPInvokeError() == 0, "could not read test window style");
            nint priorStyle = SetWindowLongPtrW(hwnd, GwlExStyle, extendedStyle | (nint)WsExNoActivate);
            Check(priorStyle != 0 || Marshal.GetLastPInvokeError() == 0, "could not set test-only WS_EX_NOACTIVATE");
            Check(SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0037), "could not apply test-only no-activation style"); // FRAMECHANGED, NOMOVE/NOSIZE/NOZORDER/NOACTIVATE
            stage.Set("hidden-window-handle-created");
            var ready = Stopwatch.StartNew();
            started = true;
            window.Show(); // Match production's WPF lifecycle; keep this test window offscreen and non-activated.
            AssertUnobtrusive(window, hwnd, activated, expectedForeground);
            await ObserveStartupAsync(window, dashboard, status, stage, () => loadedCount, TimeSpan.FromSeconds(30));
            Check(!status.Text.StartsWith("Startup failed:", StringComparison.Ordinal), status.Text);
            Check(loadedCount == 1 && window.IsLoaded && dashboard.IsLoaded &&
                PresentationSource.FromVisual(dashboard) is not null, "production Loaded/render lifecycle was not established exactly once");
            Check(dashboard.CoreWebView2 is not null, "dashboard WebView did not initialize");
            AssertProfile(dashboard, Path.Combine(cache, "dashboard"));
            Check(terminal.CoreWebView2 is null, "Terminal WebView was not lazy");
            AssertUnobtrusive(window, hwnd, activated, expectedForeground);

            string mainOrigin = Field<string>(window, "mainOrigin");
            TerminalServer terminals = Field<TerminalServer>(window, "terminals");
            string terminalOrigin = terminals.Origin;
            Check(LoopbackOrigin(mainOrigin) && LoopbackOrigin(terminalOrigin) && mainOrigin != terminalOrigin,
                "owned origins were not distinct loopback endpoints");
            AssertHardened(dashboard.CoreWebView2!, terminal: false);

            await NavigateAsync(dashboard, dashboardNavigation, mainOrigin + "/", TimeSpan.FromSeconds(15));
            await AssertDocumentAsync(dashboard, "/", "Operations", expectedTerminalLinks: 1);
            ready.Stop();
            OwnedProcess service = Field<OwnedProcess>(window, "service");
            SafeJob lifetime = Field<SafeJob>(window, "lifetime");
            Hold(held, service.ProcessId, "python-service");
            Check(lifetime.AssociatedProcessIds.Contains(service.ProcessId), "Python service was not retained by the host lifetime Job");
            IdleSample readyIdle = await SampleIdleAsync(held, host, lifetime, dashboard.CoreWebView2!);
            AssertUnobtrusive(window, hwnd, activated, expectedForeground);

            var navigationChecks = Stopwatch.StartNew();
            foreach (string route in new[] { "/observatory", "/library", "/settings" })
            {
                await NavigateAsync(dashboard, dashboardNavigation, mainOrigin + route, TimeSpan.FromSeconds(15));
                await AssertDocumentAsync(dashboard, route, route switch
                {
                    "/observatory" => "Observatory", "/library" => "Library", _ => "Settings"
                }, expectedTerminalLinks: 1);
            }
            Check(terminal.CoreWebView2 is null, "ordinary application routes eagerly initialized Terminal");
            await AssertBlockedNavigationAsync(dashboard, "https://example.invalid/", TimeSpan.FromSeconds(5));
            await AssertHttpBoundariesAsync(window, mainOrigin, terminalOrigin);

            await NavigateAsync(dashboard, dashboardNavigation, mainOrigin + "/library", TimeSpan.FromSeconds(15));
            await AssertDocumentAsync(dashboard, "/library", "Library", expectedTerminalLinks: 1);
            navigationChecks.Stop();
            IdleSample baseline = await SampleIdleAsync(held, host, lifetime, dashboard.CoreWebView2!);
            AssertUnobtrusive(window, hwnd, activated, expectedForeground);
            navigationChecks.Start();

            async Task TerminalRoundTripAsync()
            {
                dashboard.CoreWebView2!.Navigate(terminalOrigin + "/");
                await WaitUntilAsync(() => terminal.CoreWebView2 is not null && terminal.Source?.AbsoluteUri == terminalOrigin + "/" &&
                    terminal.Visibility == Visibility.Visible && dashboard.Visibility == Visibility.Collapsed,
                    TimeSpan.FromSeconds(20), "owned Terminal navigation did not show its lazy WebView");
                AssertHardened(terminal.CoreWebView2!, terminal: true);
                AssertProfile(terminal, Path.Combine(cache, "terminal"));
                await AssertDocumentAsync(terminal, "/", "Terminal", expectedTerminalLinks: 0);
                AssertUnobtrusive(window, hwnd, activated, expectedForeground);

                await NavigateAsync(terminal, terminalNavigation, mainOrigin + "/library", TimeSpan.FromSeconds(15), expectRedirect: true);
                await WaitUntilAsync(() => dashboard.Source?.AbsoluteUri == mainOrigin + "/library" &&
                    dashboard.Visibility == Visibility.Visible && terminal.Visibility == Visibility.Collapsed,
                    TimeSpan.FromSeconds(10), "return navigation did not show the Library web route");
                await AssertDocumentAsync(dashboard, "/library", "Library", expectedTerminalLinks: 1);
                AssertUnobtrusive(window, hwnd, activated, expectedForeground);
            }
            await TerminalRoundTripAsync();
            navigationChecks.Stop();

            IdleSample idle = await SampleIdleAsync(held, host, lifetime, dashboard.CoreWebView2!, terminal.CoreWebView2!);
            async Task FullRouteCycleAsync()
            {
                foreach (var (route, title) in new[] { ("/", "Operations"), ("/observatory", "Observatory"),
                    ("/library", "Library"), ("/settings", "Settings") })
                {
                    await NavigateAsync(dashboard, dashboardNavigation, mainOrigin + route, TimeSpan.FromSeconds(15));
                    await AssertDocumentAsync(dashboard, route, title, expectedTerminalLinks: 1);
                }
                await TerminalRoundTripAsync();
            }

            navigationChecks.Start();
            await FullRouteCycleAsync();
            navigationChecks.Stop();
            IdleSample repeated = await SampleIdleAsync(held, host, lifetime, dashboard.CoreWebView2!, terminal.CoreWebView2!);
            navigationChecks.Start();
            await FullRouteCycleAsync();
            navigationChecks.Stop();
            IdleSample stabilized = await SampleIdleAsync(held, host, lifetime, dashboard.CoreWebView2!, terminal.CoreWebView2!);
            string[] performanceFailures = PerformanceFailures(readyIdle, baseline, idle, repeated, stabilized);
            string webViewVersion = dashboard.CoreWebView2!.Environment.BrowserVersionString;
            AssertUnobtrusive(window, hwnd, activated, expectedForeground);

            await InvokeTask(window, "StopOwnedAsync").WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => lifetime.AssociatedProcessIds.Count == 0, TimeSpan.FromSeconds(2),
                "owned process remained in the lifetime Job after stop");
            _ = FailureIdentities(window); // Failure reporting must also tolerate already-disposed views.
            window.Close();
            await WaitUntilAsync(() => held.Values.All(x => x.HasExited), TimeSpan.FromSeconds(10),
                "a retained Python/WebView process identity remained after stop");
            nint shutdownForeground = GetForegroundWindow();
            Check(ForegroundSafe(activated, shutdownForeground, hwnd, IsTestOwnedWindow(shutdownForeground, hwnd)),
                "test window or an owned window activated during shutdown");
            DeleteGenerated(cache, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentFoundry", "WebView"), value => value.Length == 16 && value.All(char.IsAsciiHexDigitUpper));
            cacheOwned = false;
            DeleteGenerated(fixture, Path.GetTempPath(), value => value.StartsWith("agent-foundry-webview-check-", StringComparison.Ordinal) &&
                Guid.TryParseExact(value["agent-foundry-webview-check-".Length..], "N", out _));
            fixtureOwned = false;

            string manifestHash = Convert.ToHexString(SHA256.HashData(
                Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentFoundry.bundle.json")!)).ToLowerInvariant();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema_version = 1,
                bundle_manifest_sha256 = manifestHash,
                ready_milliseconds = Math.Round(ready.Elapsed.TotalMilliseconds, 1),
                navigation_checks_milliseconds = Math.Round(navigationChecks.Elapsed.TotalMilliseconds, 1),
                environment = new { os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(),
                    dotnet = RuntimeInformation.FrameworkDescription, webview = webViewVersion, logical_processors = Environment.ProcessorCount },
                quiescence_seconds = 2,
                idle_sample_seconds = 5,
                dashboard_ready_idle = readyIdle,
                baseline_idle = baseline,
                idle,
                repeated_idle = repeated,
                stabilized_idle = stabilized,
                post_warm_working_set_growth_bytes = repeated.working_set_bytes - idle.working_set_bytes,
                post_warm_private_growth_bytes = repeated.private_bytes - idle.private_bytes,
                steady_working_set_growth_bytes = stabilized.working_set_bytes - repeated.working_set_bytes,
                steady_private_growth_bytes = stabilized.private_bytes - repeated.private_bytes,
                lifecycle_pass = true,
                performance_pass = performanceFailures.Length == 0,
                performance_failures = performanceFailures,
                owned_processes_after_stop = 0,
                window_was_shown = true,
                test_window_mode = "offscreen-transparent-nonactivated",
                visible_rendering_measured = false
            }));
            Check(performanceFailures.Length == 0, "performance gate failed: " + string.Join("; ", performanceFailures));
        }
        catch (Exception error)
        {
            throw new CheckerFailure(stage.Current, FailureIdentities(window), error);
        }
        finally
        {
            if (window is not null)
            {
                if (started)
                    try { await InvokeTask(window, "StopOwnedAsync").WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { window.Close(); } catch { }
            }
            foreach (HeldProcess process in held.Values) process.Dispose();
            if (cacheOwned) try { DeleteGenerated(cache, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentFoundry", "WebView"), value => value.Length == 16 && value.All(char.IsAsciiHexDigitUpper)); } catch { }
            if (fixtureOwned) try { DeleteGenerated(fixture, Path.GetTempPath(), value => value.StartsWith("agent-foundry-webview-check-", StringComparison.Ordinal) &&
                Guid.TryParseExact(value["agent-foundry-webview-check-".Length..], "N", out _)); } catch { }
        }
    }

    static async Task ObserveStartupAsync(DesktopWindow window, WebView2 dashboard,
        System.Windows.Controls.TextBlock status, StageTracker stage, Func<int> loadedCount, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            UpdateStartupStage(window, dashboard, status, stage);
            Check(loadedCount() <= 1, "Loaded raised duplicate production startup");
            // Core initialization fires before production hardening/navigation finishes.
            if (status.Visibility == Visibility.Collapsed || status.Text.StartsWith("Startup failed:", StringComparison.Ordinal))
                return;
            if (elapsed.Elapsed >= timeout)
                throw new TimeoutException("startup remained pending at " + stage.Current);
            await Task.Delay(100);
        }
    }

    static void AssertUnobtrusive(DesktopWindow window, nint hwnd, bool activated, nint expectedForeground)
    {
        bool readable = GetWindowRect(hwnd, out NativeRect bounds);
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        NativeRect desktop = new() { Left = left, Top = top,
            Right = left + GetSystemMetrics(78), Bottom = top + GetSystemMetrics(79) };
        nint foreground = GetForegroundWindow();
        bool ownedForeground = IsTestOwnedWindow(foreground, hwnd);
        bool noActivate = (GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64() & WsExNoActivate) != 0;
        Console.Error.WriteLine("WEBVIEW_WINDOW: " + JsonSerializer.Serialize(new
        {
            bounds_readable = readable,
            bounds_pixels = new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom },
            desktop_pixels = new[] { desktop.Left, desktop.Top, desktop.Right, desktop.Bottom },
            placement_dips = new[] { window.Left, window.Top, window.ActualWidth, window.ActualHeight },
            opacity = window.Opacity, wpf_visibility = window.Visibility.ToString(), win32_visible = IsWindowVisible(hwnd),
            taskbar = window.ShowInTaskbar, show_activated = window.ShowActivated, activation_seen = activated,
            foreground_hwnd = foreground.ToInt64(), expected_foreground_hwnd = expectedForeground.ToInt64(),
            test_hwnd = hwnd.ToInt64(), native_no_activate = noActivate, owned_foreground = ownedForeground,
            outside_desktop = OffDesktop(bounds, desktop), left_of_desktop = bounds.Right <= desktop.Left
        }));
        Check(window.Opacity == 0 && !window.ShowInTaskbar && !window.ShowActivated &&
            noActivate && ForegroundSafe(activated, foreground, hwnd, ownedForeground) && readable &&
            bounds.Right > bounds.Left && bounds.Bottom > bounds.Top &&
            desktop.Right > desktop.Left && desktop.Bottom > desktop.Top && OffDesktop(bounds, desktop),
            "test window was exposed on the desktop or activated; see WEBVIEW_WINDOW predicate values");
    }

    static bool ForegroundSafe(bool activated, nint foreground, nint testWindow, bool owned) =>
        !activated && foreground != testWindow && !owned;

    static bool IsTestOwnedWindow(nint foreground, nint testWindow)
    {
        if (foreground == 0) return false; // Windows may briefly have no foreground window.
        GetWindowThreadProcessId(foreground, out uint processId);
        return foreground == testWindow || IsChild(testWindow, foreground) ||
            GetAncestor(foreground, 3) == testWindow || processId == Environment.ProcessId; // GA_ROOTOWNER
    }

    static void CheckForegroundGuard()
    {
        foreach (var (foreground, owned, activated, safe) in new (int, bool, bool, bool)[]
        {
            (11, false, false, true),
            (12, false, false, true), // Switching between unrelated apps must not fail the fixture.
            (0, false, false, true),
            (10, false, false, false),
            (12, true, false, false), // Child, owned popup, or another owned process's window.
            (12, false, true, false)
        }) Check(ForegroundSafe(activated, foreground, 10, owned) == safe,
            $"foreground guard misclassified hwnd={foreground}, owned={owned}, activated={activated}");
        Console.Error.WriteLine("WEBVIEW_FOREGROUND: 6 ownership/activation cases passed");
    }

    static bool OffDesktop(NativeRect bounds, NativeRect desktop) =>
        bounds.Right <= desktop.Left || bounds.Left >= desktop.Right ||
        bounds.Bottom <= desktop.Top || bounds.Top >= desktop.Bottom;

    sealed class NavigationJournal
    {
        string? target, observedTarget;
        ulong navigationId;
        public ulong Id => navigationId;
        TaskCompletionSource<bool>? completion;
        public (Task<bool> Completion, bool Navigate) Request(string uri, string current)
        {
            if (target == uri && completion is not null && (!completion.Task.IsCompleted ||
                (current == uri && completion.Task.IsCompletedSuccessfully && completion.Task.Result)))
                return (completion.Task, false);
            completion?.TrySetResult(false);
            target = uri; observedTarget = null; navigationId = 0;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return (completion.Task, true);
        }
        public void Starting(string uri, ulong id)
        {
            if (completion is not null && navigationId == id)
            {
                observedTarget = uri;
                return;
            }
            if (completion is not null && navigationId == 0)
            {
                if (target == uri) { navigationId = id; observedTarget = uri; }
                return;
            }
            completion?.TrySetResult(false);
            target = observedTarget = uri; navigationId = id;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void Completed(ulong id, bool success)
        {
            if (navigationId != 0 && navigationId == id) completion?.TrySetResult(success && observedTarget == target);
        }
    }

    static void CheckNavigationJournal()
    {
        var journal = new NavigationJournal();
        journal.Starting("http://127.0.0.1:8888/", 41);
        var early = journal.Request("http://127.0.0.1:8888/", "about:blank");
        Check(!early.Navigate && !early.Completion.IsCompleted, "already-started navigation must not issue a duplicate request");
        journal.Completed(40, true);
        Check(!early.Completion.IsCompleted, "unrelated navigation completed the requested route");
        journal.Completed(41, true);
        Check(early.Completion.IsCompletedSuccessfully && early.Completion.Result, "early navigation completion was lost");
        Check(!journal.Request("http://127.0.0.1:8888/", "http://127.0.0.1:8888/").Navigate, "completed current route was duplicated");
        var next = journal.Request("http://127.0.0.1:8888/library", "http://127.0.0.1:8888/");
        Check(next.Navigate, "new target was not requested");
        journal.Starting("http://127.0.0.1:8888/", 42);
        journal.Completed(42, true);
        Check(!next.Completion.IsCompleted, "previous route satisfied the new target");
        journal.Starting("http://127.0.0.1:8888/library", 43);
        journal.Completed(43, false);
        Check(next.Completion.IsCompletedSuccessfully && !next.Completion.Result, "failed navigation was treated as success");
        var redirect = journal.Request("http://127.0.0.1:8888/", "about:blank");
        journal.Starting("http://127.0.0.1:8888/", 44);
        journal.Starting("http://127.0.0.1:8888/library", 44);
        journal.Completed(44, true);
        Check(redirect.Completion.IsCompletedSuccessfully && !redirect.Completion.Result,
            "redirect to another target hung or satisfied the original request");
        var superseded = journal.Request("http://127.0.0.1:8888/", "about:blank");
        journal.Starting("http://127.0.0.1:8888/", 45);
        journal.Starting("http://127.0.0.1:8888/settings", 46);
        Check(superseded.Completion.IsCompletedSuccessfully && !superseded.Completion.Result,
            "superseding navigation abandoned a pending waiter");
        var replacement = journal.Request("http://127.0.0.1:8888/settings", "about:blank");
        journal.Completed(45, true);
        Check(!replacement.Completion.IsCompleted, "superseded completion satisfied its replacement");
        journal.Completed(46, false);
        Check(replacement.Completion.IsCompletedSuccessfully && !replacement.Completion.Result,
            "canceled replacement did not resolve as failure");
        var retry = journal.Request("http://127.0.0.1:8888/settings", "http://127.0.0.1:8888/settings");
        Check(retry.Navigate && !retry.Completion.IsCompleted, "explicit request reused canceled completion");
        var newest = journal.Request("http://127.0.0.1:8888/library", "about:blank");
        Check(retry.Completion.IsCompletedSuccessfully && !retry.Completion.Result,
            "explicit replacement abandoned an unstarted request");
        journal.Starting("http://127.0.0.1:8888/library", 47);
        journal.Starting("http://127.0.0.1:8888/library", 47);
        journal.Completed(47, true);
        Check(newest.Completion.IsCompletedSuccessfully && newest.Completion.Result,
            "same-target redirect lost a matching completion");
        Console.Error.WriteLine("WEBVIEW_NAVIGATION: early-start/redirect/supersession/cancellation cases passed");
    }

    static void CheckWebViewEnvironment()
    {
        string[] keys = ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
            "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER"];
        string?[] previous = keys.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (string key in keys) Environment.SetEnvironmentVariable(key, "fixture-override-must-be-cleared");
            DesktopProgram.ClearWebViewEnvironment();
            Check(keys.All(key => Environment.GetEnvironmentVariable(key) is null), "production WebView environment scrub did not clear all overrides");
        }
        finally
        {
            for (int i = 0; i < keys.Length; i++) Environment.SetEnvironmentVariable(keys[i], previous[i]);
        }
        string dashboard = DesktopWindow.WebViewDataDirectory(@"C:\FoundryFixture\alpha", terminal: false);
        string terminal = DesktopWindow.WebViewDataDirectory(@"C:\FoundryFixture\alpha", terminal: true);
        Check(dashboard.Length > 0 && terminal.Length > 0 && dashboard != terminal,
            "dashboard and Terminal must use distinct shared production profile paths");
        string privateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentFoundry", "WebView");
        Check(dashboard == Path.Combine(privateRoot, "11D75AD4052F4EB7", "dashboard") &&
            terminal == Path.Combine(privateRoot, "11D75AD4052F4EB7", "terminal") &&
            Path.GetFullPath(dashboard).StartsWith(Path.GetFullPath(privateRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFullPath(terminal).StartsWith(Path.GetFullPath(privateRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "production profile paths are not hashed children of the private cache root");
        Check(WebViewCacheFor(@"C:\FoundryFixture\alpha") == Path.Combine(privateRoot, "11D75AD4052F4EB7") &&
            DesktopWindow.WebViewDataDirectory(@"c:\foundryfixture\ALPHA", false) == dashboard &&
            DesktopWindow.WebViewDataDirectory(@"C:\FoundryFixture\beta", false) != dashboard,
            "checker cache derivation differs from production or aliases distinct data folders");
        Console.Error.WriteLine("WEBVIEW_ENVIRONMENT: shared scrub and hashed dashboard/Terminal profile isolation passed");
    }

    static void CheckOffDesktopGeometry()
    {
        NativeRect desktop = new() { Left = -1920, Top = -1080, Right = 1920, Bottom = 1080 };
        foreach (var (bounds, outside) in new (NativeRect, bool)[]
        {
            (new() { Left = -100, Top = -100, Right = 100, Bottom = 100 }, false),
            (new() { Left = -2020, Top = 0, Right = -1920, Bottom = 100 }, true),
            (new() { Left = 1920, Top = 0, Right = 2020, Bottom = 100 }, true),
            (new() { Left = 0, Top = -1180, Right = 100, Bottom = -1080 }, true),
            (new() { Left = 0, Top = 1080, Right = 100, Bottom = 1180 }, true),
            (new() { Left = 1919, Top = 0, Right = 2020, Bottom = 100 }, false),
            (new() { Left = -2020, Top = -1180, Right = 2020, Bottom = 1180 }, false)
        }) Check(OffDesktop(bounds, desktop) == outside, "off-desktop geometry misclassified intersection");
        Console.Error.WriteLine("WEBVIEW_GEOMETRY: 7 intersection cases passed");
    }

    static void UpdateStartupStage(DesktopWindow window, WebView2 dashboard,
        System.Windows.Controls.TextBlock status, StageTracker stage)
    {
        if (status.Text.StartsWith("Startup failed:", StringComparison.Ordinal)) stage.Set("startup-reported-failure");
        else if (dashboard.CoreWebView2 is not null) stage.Set("dashboard-core-ready");
        else if (FieldOrNull<TerminalServer>(window, "terminals") is not null)
            stage.Set(dashboard.CreationProperties is null ? "dashboard-environment-pending" : "dashboard-controller-pending");
        else if (!string.IsNullOrEmpty(Field<string>(window, "mainOrigin"))) stage.Set("terminal-server-pending");
        else if (FieldOrNull<OwnedProcess>(window, "service") is not null) stage.Set("python-handshake-pending");
        else if (FieldOrNull<VerifiedLaunch>(window, "bundle") is not null) stage.Set("bundle-verified");
        else stage.Set("bundle-verification-pending");
    }

    static object[] FailureIdentities(DesktopWindow? window)
    {
        var roles = new Dictionary<int, HashSet<string>> { [Environment.ProcessId] = ["checker-host"] };
        if (window is not null)
        {
            OwnedProcess? service = FieldOrNull<OwnedProcess>(window, "service");
            if (service is not null) AddRole(roles, service.ProcessId, "python-service");
            try
            {
                foreach (int id in Field<SafeJob>(window, "lifetime").AssociatedProcessIds) AddRole(roles, id, "lifetime-job");
            }
            catch { }
            foreach ((WebView2? view, string name) in new[]
            {
                (FieldOrNull<WebView2>(window, "dashboard"), "dashboard"),
                (FieldOrNull<WebView2>(window, "terminalView"), "terminal")
            })
            {
                try
                {
                    if (view?.CoreWebView2 is null) continue;
                    foreach (CoreWebView2ProcessInfo item in view.CoreWebView2.Environment.GetProcessInfos())
                        AddRole(roles, item.ProcessId, name + ":" + item.Kind);
                }
                catch { }
            }
        }
        return roles.OrderBy(x => x.Key).Select(x =>
        {
            try
            {
                using Process process = Process.GetProcessById(x.Key);
                _ = process.SafeHandle;
                return (object)new { pid = process.Id, name = process.ProcessName,
                    start_time_utc = process.StartTime.ToUniversalTime().ToString("O"), roles = x.Value.Order() };
            }
            catch (Exception error)
            {
                return new { pid = x.Key, name = "unreadable", start_time_utc = "", roles = x.Value.Order(),
                    inspection_error = error.GetType().Name };
            }
        }).ToArray();
    }

    static void AddRole(Dictionary<int, HashSet<string>> roles, int id, string role)
    {
        if (!roles.TryGetValue(id, out HashSet<string>? values)) roles.Add(id, values = []);
        values.Add(role);
    }

    static async Task NavigateAsync(WebView2 view, NavigationJournal journal, string uri, TimeSpan timeout, bool expectRedirect = false)
    {
        if (expectRedirect)
        {
            view.CoreWebView2.Navigate(uri);
            return;
        }
        var elapsed = Stopwatch.StartNew();
        var request = journal.Request(uri, view.CoreWebView2.Source);
        try
        {
            Console.Error.WriteLine("WEBVIEW_NAVIGATE: " + JsonSerializer.Serialize(new
                { target = uri, current = view.CoreWebView2.Source, issue_navigation = request.Navigate, existing_id = journal.Id }));
            if (request.Navigate) view.CoreWebView2.Navigate(uri);
            bool success = await request.Completion.WaitAsync(timeout);
            Check(success && view.Source?.AbsoluteUri == uri, "web route did not complete: " + uri);
            TimeSpan remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("navigation deadline elapsed before document readiness");
            string readiness = await view.CoreWebView2.ExecuteScriptAsync("document.readyState").WaitAsync(remaining);
            Check(readiness == "\"complete\"", "matching navigation did not leave a completed document: " + uri);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("WEBVIEW_NAV_TIMEOUT: " + JsonSerializer.Serialize(new
                { target = uri, current = view.CoreWebView2.Source, awaited_id = journal.Id }));
            try
            {
                // Read-only post-failure evidence; never turns a timed-out navigation into a pass.
                string state = await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({ready:document.readyState,path:location.pathname," +
                    "resources:performance.getEntriesByType('resource').slice(0,16).map(r=>({path:new URL(r.name).pathname,type:r.initiatorType,duration:r.duration}))})")
                    .WaitAsync(TimeSpan.FromSeconds(1));
                Console.Error.WriteLine("WEBVIEW_DOCUMENT: " + state);
            }
            catch (Exception error) { Console.Error.WriteLine("WEBVIEW_DOCUMENT_UNAVAILABLE: " + error.GetType().Name); }
            throw;
        }
    }

    static async Task AssertBlockedNavigationAsync(WebView2 view, string uri, TimeSpan timeout)
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Starting(object? _, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri == uri) observed.TrySetResult(e.Cancel);
        }
        view.CoreWebView2.NavigationStarting += Starting;
        try
        {
            view.CoreWebView2.Navigate(uri);
            Check(await observed.Task.WaitAsync(timeout), "foreign navigation was not canceled before network access");
        }
        finally { view.CoreWebView2.NavigationStarting -= Starting; }
    }

    static async Task AssertDocumentAsync(WebView2 view, string path, string titlePart, int expectedTerminalLinks)
    {
        string script = "(()=>JSON.stringify({ready:document.readyState,path:location.pathname,title:document.title," +
            "nav:[...document.querySelectorAll('.app-nav a')].map(a=>new URL(a.href).pathname)," +
            "terminal:[...document.querySelectorAll('.app-nav a')].filter(a=>a.textContent.trim()==='Terminal').length}))()";
        JsonDocument? document = null;
        await WaitUntilAsync(async () =>
        {
            string encoded = await view.CoreWebView2.ExecuteScriptAsync(script);
            string? json = JsonSerializer.Deserialize<string>(encoded);
            document?.Dispose();
            document = json is null ? null : JsonDocument.Parse(json);
            if (document is null) return false;
            JsonElement root = document.RootElement;
            string[] nav = root.GetProperty("nav").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            return root.GetProperty("ready").GetString() == "complete" && root.GetProperty("path").GetString() == path &&
                root.GetProperty("title").GetString()!.Contains("Agentopia", StringComparison.Ordinal) &&
                root.GetProperty("title").GetString()!.Contains(titlePart, StringComparison.Ordinal) &&
                new[] { "/", "/observatory", "/library", "/settings" }.All(nav.Contains) &&
                root.GetProperty("terminal").GetInt32() == expectedTerminalLinks;
        }, TimeSpan.FromSeconds(5), "document did not initialize the expected route/navigation: " + path);
        document?.Dispose();
    }

    static async Task AssertHttpBoundariesAsync(DesktopWindow window, string mainOrigin, string terminalOrigin)
    {
        string mainKey = Field<string>(window, "mainKey"), terminalKey = Field<string>(window, "terminalKey");
        Check(await StatusAsync(mainOrigin + "/", null, null) == HttpStatusCode.Forbidden,
            "dashboard accepted a request without its private header");
        Check(await StatusAsync(terminalOrigin + "/", null, null) == HttpStatusCode.Forbidden,
            "Terminal accepted a request without its private header");
        Check(await StatusAsync(mainOrigin + "/", "X-Foundry-Desktop", mainKey) == HttpStatusCode.OK,
            "dashboard rejected its private header");
        Check(await StatusAsync(terminalOrigin + "/", TerminalServer.AuthorizationHeader, terminalKey) == HttpStatusCode.OK,
            "Terminal rejected its private header");
        Check(await StatusAsync(mainOrigin + "/", TerminalServer.AuthorizationHeader, terminalKey) == HttpStatusCode.Forbidden &&
              await StatusAsync(terminalOrigin + "/", "X-Foundry-Desktop", mainKey) == HttpStatusCode.Forbidden,
            "owned origins accepted one another's private headers");
    }

    static async Task<HttpStatusCode> StatusAsync(string uri, string? header, string? value)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (header is not null) request.Headers.TryAddWithoutValidation(header, value);
        using HttpResponseMessage response = await client.SendAsync(request);
        return response.StatusCode;
    }

    static void AssertHardened(CoreWebView2 core, bool terminal)
    {
        CoreWebView2Settings settings = core.Settings;
        Check(!settings.AreDevToolsEnabled && !settings.AreDefaultContextMenusEnabled &&
              !settings.AreHostObjectsAllowed && settings.IsWebMessageEnabled == terminal &&
              !settings.IsPasswordAutosaveEnabled && !settings.IsGeneralAutofillEnabled &&
              !settings.IsStatusBarEnabled && !settings.AreBrowserAcceleratorKeysEnabled,
            "WebView settings do not match the production resource boundary");
    }

    static void AssertProfile(WebView2 view, string expectedDirectory) =>
        Check(string.Equals(Path.GetFullPath(view.CoreWebView2.Environment.UserDataFolder), Path.GetFullPath(expectedDirectory),
                StringComparison.OrdinalIgnoreCase) && view.CoreWebView2.Profile.IsInPrivateModeEnabled,
            "WebView did not use its fixture-isolated production InPrivate profile");

    static void CaptureWebViewProcesses(Dictionary<int, HeldProcess> held, CoreWebView2 core, string role)
    {
        foreach (CoreWebView2ProcessInfo item in core.Environment.GetProcessInfos()) Hold(held, item.ProcessId, role + ":" + item.Kind);
    }

    static int[] WebViewProcessIds(params CoreWebView2[] cores) => cores
        .SelectMany(x => x.Environment.GetProcessInfos()).Select(x => x.ProcessId).Distinct().Order().ToArray();

    static void Hold(Dictionary<int, HeldProcess> held, int processId, string role)
    {
        if (!held.TryGetValue(processId, out HeldProcess? process)) held.Add(processId, process = new HeldProcess(processId));
        else Check(process.IsSameProcess, "a retained process ID was reused or became unreadable");
        process.Roles.Add(role);
    }

    static Dictionary<int, double> CpuSnapshot(IEnumerable<HeldProcess> processes) =>
        processes.Where(x => x.IsSameProcess).ToDictionary(x => x.Id, x => x.CpuSeconds);

    sealed record ProcessSample(string name, int pid, string start_time_utc, string[] roles,
        double cpu_seconds, double cpu_machine_percent, long working_set_bytes, long private_bytes);
    sealed record IdleSample(double duration_seconds, double cpu_seconds, double cpu_machine_percent,
        long working_set_bytes, long private_bytes, ProcessSample[] processes);

    static string[] PerformanceFailures(IdleSample ready, IdleSample dashboard, IdleSample warm, IdleSample repeated,
        IdleSample stabilized)
    {
        var failures = new List<string>();
        foreach (var (name, sample, memoryLimit) in new[] { ("dashboard-ready", ready, 600_000_000L),
            ("dashboard-routes", dashboard, 600_000_000L), ("warm", warm, 1_073_741_824L),
            ("repeated", repeated, 1_073_741_824L), ("stabilized", stabilized, 1_073_741_824L) })
        {
            if (sample.cpu_seconds > 0.25) failures.Add(name + " CPU exceeds 0.25 seconds per five-second sample");
            if (sample.working_set_bytes >= memoryLimit) failures.Add(name + " working set reaches its memory ceiling");
        }
        // WebView may commit lazily on its first repeated route pass; bound that transition, then require tight steady state.
        if (repeated.working_set_bytes - warm.working_set_bytes > 67_108_864) failures.Add("post-warm working-set growth exceeds 64 MiB");
        if (repeated.private_bytes - warm.private_bytes > 67_108_864) failures.Add("post-warm private-commit growth exceeds 64 MiB");
        if (stabilized.working_set_bytes - repeated.working_set_bytes > 16_777_216) failures.Add("steady working-set growth exceeds 16 MiB");
        if (stabilized.private_bytes - repeated.private_bytes > 16_777_216) failures.Add("steady private-commit growth exceeds 16 MiB");
        if (!warm.processes.Select(x => (x.pid, x.start_time_utc)).Order()
            .SequenceEqual(repeated.processes.Select(x => (x.pid, x.start_time_utc)).Order()))
            failures.Add("process identities/count changed across post-warm route cycle");
        if (!repeated.processes.Select(x => (x.pid, x.start_time_utc)).Order()
            .SequenceEqual(stabilized.processes.Select(x => (x.pid, x.start_time_utc)).Order()))
            failures.Add("process identities/count changed across steady route cycle");
        return failures.ToArray();
    }

    static void CheckPerformanceGate()
    {
        var sample = new IdleSample(5, 0.25, 0, 500_000_000, 200_000_000, []);
        Check(PerformanceFailures(sample, sample, sample, sample, sample).Length == 0, "exact CPU ceiling must pass");
        Check(PerformanceFailures(sample, sample with { cpu_seconds = 0.250001 }, sample, sample, sample).Length > 0,
            "aggregate CPU over budget was accepted");
        Check(PerformanceFailures(sample with { working_set_bytes = 600_000_000 }, sample, sample, sample, sample).Length > 0,
            "dashboard memory ceiling was not strict");
        Check(PerformanceFailures(sample, sample, sample with { working_set_bytes = 1_073_741_824 }, sample, sample).Length > 0,
            "warm memory ceiling was not strict");
        Check(PerformanceFailures(sample, sample, sample, sample, sample with { working_set_bytes = 516_777_216,
            private_bytes = 216_777_216 }).Length == 0, "exact growth ceiling must pass");
        Check(PerformanceFailures(sample, sample, sample, sample, sample with { working_set_bytes = 516_777_217 }).Length > 0,
            "working-set growth over budget was accepted");
        Check(PerformanceFailures(sample, sample, sample, sample, sample with { private_bytes = 216_777_217 }).Length > 0,
            "private-commit growth over budget was accepted");
        Check(PerformanceFailures(sample, sample, sample,
            sample with { working_set_bytes = 567_108_864, private_bytes = 267_108_864 }, sample).Length == 0,
            "exact post-warm growth ceiling must pass");
        Check(PerformanceFailures(sample, sample, sample,
            sample with { working_set_bytes = 567_108_865 }, sample).Length > 0,
            "post-warm working-set growth over budget was accepted");
        Check(PerformanceFailures(sample, sample, sample,
            sample with { private_bytes = 267_108_865 }, sample).Length > 0,
            "post-warm private-commit growth over budget was accepted");
        var identity = new ProcessSample("fixture", 1, "2026-09-13T00:00:00Z", [], 0, 0, 0, 0);
        Check(PerformanceFailures(sample, sample, sample with { processes = [identity] }, sample, sample).Length > 0,
            "new process in the post-warm route cycle was accepted");
        Check(PerformanceFailures(sample, sample, sample, sample with { processes = [identity] },
            sample with { processes = [identity with { start_time_utc = "2026-09-13T00:00:01Z" }] }).Length > 0,
            "reused process ID was accepted as a stable warm topology");
        Check(VisibleMemoryPass(699_999_999, 449_999_999),
            "values strictly below both active memory ceilings were rejected");
        Check(!VisibleMemoryPass(700_000_000, 400_000_000), "active working-set ceiling was not strict");
        Check(!VisibleMemoryPass(650_000_000, 450_000_000), "active private-commit ceiling was not strict");
        Console.Error.WriteLine("WEBVIEW_PERFORMANCE: 15 CPU/memory/growth/identity cases passed");
    }

    static async Task<IdleSample> SampleIdleAsync(Dictionary<int, HeldProcess> held, HeldProcess host, SafeJob lifetime,
        params CoreWebView2[] cores)
    {
        // Fixed post-ready settling interval; never wait for a favorable activity reading.
        await Task.Delay(TimeSpan.FromSeconds(2));
        int[] jobBefore = lifetime.AssociatedProcessIds.Order().ToArray();
        foreach (int id in jobBefore) Hold(held, id, "lifetime-job");
        for (int i = 0; i < cores.Length; i++) CaptureWebViewProcesses(held, cores[i], i == 0 ? "dashboard" : "terminal");
        int[] webViewBefore = WebViewProcessIds(cores);
        HeldProcess[] measured = held.Values.Append(host).ToArray();
        Check(measured.All(x => x.IsSameProcess), "a measured identity was stale before idle sampling");
        var before = CpuSnapshot(measured);
        var elapsed = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(5));
        elapsed.Stop();
        Check(jobBefore.SequenceEqual(lifetime.AssociatedProcessIds.Order()) && webViewBefore.SequenceEqual(WebViewProcessIds(cores)),
            "Job or WebView process membership changed during idle sampling");
        foreach (HeldProcess process in measured) process.Refresh();
        Check(measured.All(x => x.IsSameProcess), "a measured identity changed during idle sampling");
        var after = CpuSnapshot(measured);
        Check(before.Keys.Order().SequenceEqual(after.Keys.Order()), "CPU sample membership changed");
        ProcessSample[] rows = measured.Select(process =>
        {
            double cpu = after[process.Id] - before[process.Id];
            Check(cpu >= 0, "a retained process CPU counter moved backwards");
            return new ProcessSample(process.Name, process.Id, process.StartTime.ToString("O"), process.Roles.Order().ToArray(),
                Math.Round(cpu, 6), Math.Round(100 * cpu / elapsed.Elapsed.TotalSeconds / Environment.ProcessorCount, 4),
                process.WorkingSet, process.PrivateBytes);
        }).OrderBy(x => x.pid).ToArray();
        double totalCpu = rows.Sum(x => x.cpu_seconds);
        return new IdleSample(Math.Round(elapsed.Elapsed.TotalSeconds, 6), totalCpu,
            Math.Round(100 * totalCpu / elapsed.Elapsed.TotalSeconds / Environment.ProcessorCount, 4),
            rows.Sum(x => x.working_set_bytes), rows.Sum(x => x.private_bytes), rows);
    }

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed >= timeout) throw new TimeoutException(failure);
            await Task.Delay(50);
        }
    }

    static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed >= timeout) throw new TimeoutException(failure);
            await Task.Delay(50);
        }
    }

    static Task InvokeTask(object target, string name) =>
        (Task)(target.GetType().GetMethod(name, PrivateInstance)?.Invoke(target, null)
            ?? throw new MissingMethodException(target.GetType().FullName, name));

    static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, PrivateInstance)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().FullName, name));

    static T? FieldOrNull<T>(object target, string name) where T : class =>
        target.GetType().GetField(name, PrivateInstance)?.GetValue(target) as T;

    static bool LoopbackOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port is >= 1024 and <= 65535 &&
        uri.PathAndQuery == "/" && uri.UserInfo.Length == 0;

    static string WebViewCacheFor(string fixture) =>
        Directory.GetParent(DesktopWindow.WebViewDataDirectory(fixture, terminal: false))!.FullName;

    static void WriteFixture(string fixture)
    {
        Directory.CreateDirectory(fixture);
        string state = Path.Combine(fixture, "state.json");
        File.WriteAllText(state, "{\"sessions\":[]}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture, "settings.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1,
            config = new
            {
                adapter = "json", codex_home = "", state_file = state, host = "127.0.0.1", port = 8777,
                open = false, verbose = false
            }
        }), new UTF8Encoding(false));
    }

    static void DeleteGenerated(string path, string expectedParent, Func<string, bool> validName)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedParent));
        Check(string.Equals(Directory.GetParent(full)?.FullName, parent, StringComparison.OrdinalIgnoreCase) &&
              validName(Path.GetFileName(full)), "refusing to delete an unexpected fixture path");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    static void Check(bool value, string failure)
    {
        if (!value) throw new InvalidOperationException(failure);
    }

    sealed class HeldProcess : IDisposable
    {
        readonly Process process;
        public HeldProcess(int id)
        {
            process = Process.GetProcessById(id);
            _ = process.SafeHandle;
            Id = process.Id; Name = process.ProcessName; StartTime = process.StartTime.ToUniversalTime();
        }
        public int Id { get; }
        public string Name { get; }
        public DateTime StartTime { get; }
        public HashSet<string> Roles { get; } = [];
        public bool IsSameProcess
        {
            get
            {
                try { return !process.HasExited && process.StartTime.ToUniversalTime() == StartTime; }
                catch { return false; }
            }
        }
        public bool HasExited => process.HasExited;
        public double CpuSeconds => process.TotalProcessorTime.TotalSeconds;
        public long WorkingSet => process.WorkingSet64;
        public long PrivateBytes => process.PrivateMemorySize64;
        public void Refresh() => process.Refresh();
        public void Dispose() => process.Dispose();
    }

    sealed class StageTracker
    {
        string current = "checker-created";
        public string Current => Volatile.Read(ref current);
        public void Set(string value)
        {
            if (Interlocked.Exchange(ref current, value) != value) Console.Error.WriteLine("WEBVIEW_STAGE: " + value);
        }
    }

    sealed class CheckerFailure : Exception
    {
        public CheckerFailure(string stage, object[] identities, Exception inner) : base(
            "stage=" + stage + "; process_identities=" + JsonSerializer.Serialize(identities), inner) { }
    }

    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] static extern nint GetActiveWindow();
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(nint hwnd, nint insertAfter,
        int x, int y, int width, int height, uint flags);
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetDefaultDllDirectories(uint flags);
}
