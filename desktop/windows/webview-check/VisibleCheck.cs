using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using AgentFoundry.Desktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WebViewCheck;

static partial class Program
{
    const long VisibleWorkingSetLimit = 700_000_000;
    const long VisiblePrivateCommitLimit = 450_000_000;

    static string VisibleFixture(int tick) => JsonSerializer.Serialize(new
    {
        sessions = Enumerable.Range(0, 24).Select(i => new
        {
            id = "fixture-" + i, parent = i % 6 == 0 ? null : "fixture-" + (i / 6 * 6),
            name = "Synthetic agent " + i, cwd = "C:/Synthetic/Project-" + (i / 6),
            status = "working", task = "Synthetic rendering workload",
            current_action = "Synthetic fixture tick " + tick,
            harness = "synthetic", model = "not-a-real-model"
        })
    });

    static void CheckVisibleFixture()
    {
        using var document = JsonDocument.Parse(VisibleFixture(7));
        var rows = document.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
        Check(rows.Length == 24, "visible fixture must contain exactly 24 synthetic agents");
        Check(rows.Select(x => x.GetProperty("cwd").GetString()).Distinct().Count() == 4,
            "visible fixture must have four project roots");
        Check(rows.Count(x => x.GetProperty("parent").ValueKind == JsonValueKind.Null) == 4 &&
            rows.All(x => x.GetProperty("status").GetString() == "working" &&
                x.GetProperty("current_action").GetString() == "Synthetic fixture tick 7"),
            "visible fixture hierarchy or one-second update data is incorrect");
        Console.Error.WriteLine("VISIBLE_FIXTURE: 24 agents/four roots/synthetic updates passed");
        var primary = new InvalidOperationException("primary fixture failure");
        var cleanup = new IOException("cleanup fixture failure");
        var secondCleanup = new TimeoutException("second cleanup fixture failure");
        Check(CombineVisibleErrors(primary, []) == primary && CombineVisibleErrors(null, []) is null,
            "successful cleanup lost the original result");
        var combined = CombineVisibleErrors(primary, [cleanup, secondCleanup]) as AggregateException;
        Check(combined is not null && combined.InnerExceptions.SequenceEqual(new Exception[] { primary, cleanup, secondCleanup }),
            "cleanup error replaced the primary visible result");
        Check(CombineVisibleErrors(null, [cleanup]) is AggregateException, "cleanup-only failure was ignored");
        Console.Error.WriteLine("VISIBLE_CLEANUP: primary and cleanup failures remain separately recoverable");
        using var browser = JsonDocument.Parse("{\"document_has_focus\":false,\"document_hidden\":true,\"paused\":true,\"reduced_motion\":false,\"motion_active\":false}");
        using var boundary = JsonDocument.Parse(VisibleBoundaryJson(new(false, true, false, "intersecting_window"), browser.RootElement));
        Check(boundary.RootElement.TryGetProperty("window", out var native) &&
            !native.GetProperty("foreground_matches").GetBoolean() && native.GetProperty("visible").GetBoolean() &&
            !native.GetProperty("minimized").GetBoolean() && native.GetProperty("occlusion_reason").GetString() == "intersecting_window" &&
            !native.GetProperty("unobscured").GetBoolean() && boundary.RootElement.GetProperty("browser").GetRawText() == browser.RootElement.GetRawText(),
            "visible boundary omitted or conflated native/browser diagnostic fields");
        Check(new NativeVisibilityState(true, true, false, "clear").unobscured &&
            !new NativeVisibilityState(false, true, false, "clear").unobscured &&
            !new NativeVisibilityState(true, false, false, "clear").unobscured &&
            !new NativeVisibilityState(true, true, true, "clear").unobscured &&
            !new NativeVisibilityState(true, true, false, "z_order_incomplete").unobscured,
            "reason diagnostics weakened the visible validity predicate");
        Console.Error.WriteLine("VISIBLE_BOUNDARY: distinct native/browser fields and unchanged visibility validity cases passed");
        Check(boundary.RootElement.TryGetProperty("readiness_failures", out var reasons) &&
            reasons.EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] {
                "native_not_foreground", "native_occlusion:intersecting_window", "document_unfocused", "document_hidden", "motion_paused", "motion_inactive" }),
            "readiness gate must identify every native/browser blocker before sampling");
        foreach (string field in new[] { "document_has_focus", "document_hidden", "paused", "motion_active", "ready" })
        {
            using var state = JsonDocument.Parse(JsonSerializer.Serialize(new {
                document_has_focus = field != "document_has_focus", document_hidden = field == "document_hidden",
                paused = field == "paused", reduced_motion = true, motion_active = field != "motion_active" }));
            using var result = JsonDocument.Parse(VisibleBoundaryJson(new(true, true, false, "clear"), state.RootElement));
            Check(result.RootElement.GetProperty("readiness_failures").GetArrayLength() == (field == "ready" ? 0 : 1),
                "readiness must require each browser condition but permit the unchanged reduced-motion preference");
        }
        Console.Error.WriteLine("VISIBLE_READINESS: all blockers and reduced-motion-compatible ready state passed");
        foreach (string json in new[] { "null", "[]", "false", "true", "42", "\"text\"", "{}", "{\"document_has_focus\":null}" })
        {
            using var unavailable = JsonDocument.Parse(json);
            using var result = JsonDocument.Parse(VisibleBoundaryJson(new(false, true, false, "intersecting_window"), unavailable.RootElement));
            Check(result.RootElement.GetProperty("browser").GetRawText() == unavailable.RootElement.GetRawText() &&
                result.RootElement.GetProperty("window").GetProperty("visible").GetBoolean() &&
                result.RootElement.GetProperty("readiness_failures").EnumerateArray().Select(x => x.GetString()).SequenceEqual(
                    new[] { "native_not_foreground", "native_occlusion:intersecting_window", "browser_probe_unavailable" }),
                "unavailable browser probe must preserve native evidence and fail closed: " + json);
            Check(VisibleReadinessFailures(new(true, true, false, "clear"), unavailable.RootElement).SequenceEqual(new[] { "browser_probe_unavailable" }),
                "an unobscured window cannot make an unavailable browser probe ready");
        }
        Console.Error.WriteLine("VISIBLE_UNAVAILABLE_PROBE: null, array, booleans, number, string and incomplete objects fail closed with native evidence");
        var sequence = new List<string>();
        ShowVisibleAfterConfirmation(() => { sequence.Add("confirm"); return true; }, () => sequence.Add("show"));
        Check(sequence.SequenceEqual(new[] { "confirm", "show" }), "visible window must be shown exactly once after operator confirmation");
        sequence.Clear();
        bool canceled = false;
        try { ShowVisibleAfterConfirmation(() => { sequence.Add("cancel"); return false; }, () => sequence.Add("show")); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && sequence.SequenceEqual(new[] { "cancel" }), "canceled operator gate must prevent the visible test from starting");
        Console.Error.WriteLine("VISIBLE_OPERATOR_GATE: confirm-before-show exactly once; cancellation prevents show");
    }

    static void ShowVisibleAfterConfirmation(Func<bool> confirm, Action show)
    {
        if (!confirm()) throw new OperationCanceledException("visible test operator gate canceled or timed out; no test window shown");
        show();
    }

    static bool ConfirmVisibleStart()
    {
        var prompt = new Window { Title = "Observatory test — click OK to start", SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var content = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new System.Windows.Controls.TextBlock { Width = 440, TextWrapping = TextWrapping.Wrap,
            Text = "Click OK, then leave Observatory in front until it closes automatically. This runs one synthetic test only; do not switch windows.\n\nClick within 20 seconds or this attempt will cancel." });
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(8) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => prompt.DialogResult = true;
        buttons.Children.Add(ok); buttons.Children.Add(cancel); content.Children.Add(buttons); prompt.Content = content;
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timeout.Tick += (_, _) => prompt.DialogResult = false;
        timeout.Start();
        try { return prompt.ShowDialog() == true; }
        finally { timeout.Stop(); }
    }

    sealed record NativeVisibilityState(bool foreground_matches, bool visible, bool minimized, string occlusion_reason)
    {
        public bool unobscured => foreground_matches && visible && !minimized && occlusion_reason == "clear";
    }

    static string VisibleBoundaryJson(NativeVisibilityState window, JsonElement browser) =>
        JsonSerializer.Serialize(new { window, browser, readiness_failures = VisibleReadinessFailures(window, browser) });

    static string[] VisibleReadinessFailures(NativeVisibilityState window, JsonElement browser)
    {
        var failures = new List<string>();
        if (!window.foreground_matches) failures.Add("native_not_foreground");
        if (!window.visible) failures.Add("native_hidden");
        if (window.minimized) failures.Add("native_minimized");
        if (window.occlusion_reason != "clear") failures.Add("native_occlusion:" + window.occlusion_reason);
        if (browser.ValueKind != JsonValueKind.Object ||
            new[] { "document_has_focus", "document_hidden", "paused", "reduced_motion", "motion_active" }.Any(name =>
                !browser.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
        {
            failures.Add("browser_probe_unavailable");
            return failures.ToArray();
        }
        if (!browser.GetProperty("document_has_focus").GetBoolean()) failures.Add("document_unfocused");
        if (browser.GetProperty("document_hidden").GetBoolean()) failures.Add("document_hidden");
        if (browser.GetProperty("paused").GetBoolean()) failures.Add("motion_paused");
        if (!browser.GetProperty("motion_active").GetBoolean()) failures.Add("motion_inactive");
        return failures.ToArray();
    }

    static async Task<JsonElement> EvaluateVisibleScriptAsync(WebView2 view, NavigationJournal navigation, string boundary, string script)
    {
        var core = view.CoreWebView2;
        string Source() => Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "";
        async Task<JsonElement> Evaluate(string phase, string code)
        {
            string sourceBefore = Source();
            ulong navigationBefore = navigation.Id;
            try
            {
                CoreWebView2ExecuteScriptResult result = await core.ExecuteScriptWithResultAsync(code).WaitAsync(TimeSpan.FromSeconds(2));
                var exception = result.Succeeded ? null : result.Exception;
                Console.Error.WriteLine("VISIBLE_SCRIPT_RESULT: " + JsonSerializer.Serialize(new {
                    boundary, phase, succeeded = result.Succeeded, result_as_json = result.ResultAsJson,
                    exception = exception is null ? null : new { name = exception.Name, message = exception.Message[..Math.Min(exception.Message.Length, 512)],
                        line = exception.LineNumber, column = exception.ColumnNumber },
                    source_before = sourceBefore, source_after = Source(), navigation_id_before = navigationBefore, navigation_id_after = navigation.Id
                }));
                using var json = JsonDocument.Parse(result.Succeeded ? result.ResultAsJson : "null");
                return json.RootElement.Clone();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("VISIBLE_SCRIPT_TRANSPORT_FAILED: " + JsonSerializer.Serialize(new {
                    boundary, phase, error_type = error.GetType().Name, source_before = sourceBefore, source_after = Source(),
                    navigation_id_before = navigationBefore, navigation_id_after = navigation.Id }));
                throw;
            }
        }
        string source = Source();
        ulong navigationId = navigation.Id;
        var before = await Evaluate("context-before", ReadVisibleContext);
        var value = await Evaluate("probe", script);
        var after = await Evaluate("context-after", ReadVisibleContext);
        bool contextStable = source == Source() && navigationId == navigation.Id && before.ValueKind == JsonValueKind.Object &&
            after.ValueKind == JsonValueKind.Object && new[] { "url", "ready", "marker" }.All(name =>
                before.TryGetProperty(name, out var first) && after.TryGetProperty(name, out var last) &&
                first.ValueKind == JsonValueKind.String && last.ValueKind == JsonValueKind.String && first.GetString() == last.GetString());
        Console.Error.WriteLine("VISIBLE_SCRIPT_CONTEXT: " + JsonSerializer.Serialize(new { boundary, stable = contextStable, before, after }));
        return contextStable ? value : JsonSerializer.SerializeToElement<object?>(null);
    }

    static Exception? CombineVisibleErrors(Exception? primary, List<Exception> cleanup) =>
        cleanup.Count == 0 ? primary : new AggregateException("Visible cleanup failed",
            primary is null ? cleanup : new[] { primary }.Concat(cleanup));

    static async Task DeleteVisibleGeneratedAsync(string path, string parent, string name)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { DeleteGenerated(path, parent, value => value == name); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException &&
                elapsed.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(100); }
        }
    }

    static async Task CheckVisibleCleanupAsync()
    {
        string name = "agent-foundry-webview-check-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), name);
        Check(!Directory.Exists(path), "cleanup check fixture already exists");
        Directory.CreateDirectory(path);
        FileStream? locked = null;
        Task release = Task.CompletedTask;
        try
        {
            locked = new FileStream(Path.Combine(path, "locked.test"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            bool refused = false;
            try { await DeleteVisibleGeneratedAsync(path, Path.GetTempPath(), name + "-wrong"); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && Directory.Exists(path), "cleanup deleted a non-matching exact path");
            var file = locked;
            release = Task.Run(async () => { await Task.Delay(150); file.Dispose(); });
            await DeleteVisibleGeneratedAsync(path, Path.GetTempPath(), name);
            await release;
            Check(!Directory.Exists(path), "cleanup did not retry after the file lock was released");
            Directory.CreateDirectory(path);
            locked = new FileStream(Path.Combine(path, "locked.test"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var elapsed = Stopwatch.StartNew();
            bool failed = false;
            try { await DeleteVisibleGeneratedAsync(path, Path.GetTempPath(), name); }
            catch (IOException) { failed = true; }
            Check(failed && Directory.Exists(path) && elapsed.Elapsed >= TimeSpan.FromSeconds(1.5) &&
                elapsed.Elapsed < TimeSpan.FromSeconds(4), "persistent file lock was ignored or cleanup retry was not bounded");
            Console.Error.WriteLine("VISIBLE_CLEANUP_PATHS: exact-path refusal, released-lock retry and bounded persistent-lock failure passed");
        }
        finally
        {
            await release;
            locked?.Dispose();
            DeleteGenerated(path, Path.GetTempPath(), value => value == name);
        }
    }

    static async Task RunVisibleAsync()
    {
        var runClock = Stopwatch.StartNew();
        Native.EnsureWindowsAndNonElevated();
        CheckVisibleFixture();
        string fixture = Path.Combine(Path.GetTempPath(), "agent-foundry-webview-check-" + Guid.NewGuid().ToString("N"));
        string cache = WebViewCacheFor(fixture);
        Check(!Directory.Exists(fixture) && !Directory.Exists(cache), "visible fixture/cache already existed");
        DesktopWindow? window = null;
        var held = new Dictionary<int, HeldProcess>();
        using var host = new HeldProcess(Environment.ProcessId);
        host.Roles.Add("checker-host");
        using var updates = new CancellationTokenSource();
        Task updater = Task.CompletedTask;
        int ticks = 0;
        var stage = new StageTracker();
        Exception? primary = null;
        var cleanupErrors = new List<Exception>();
        int? ownedProcessesAfterStop = null;
        bool childrenExited = false;
        async Task Cleanup(string name, Func<Task> action)
        {
            try { await action(); }
            catch (Exception error)
            {
                cleanupErrors.Add(new InvalidOperationException(name, error));
                Console.Error.WriteLine("VISIBLE_CLEANUP_FAILED: " + name + ": " + error);
            }
        }
        try
        {
            WriteFixture(fixture);
            void WriteTick()
            {
                string temporary = Path.Combine(fixture, "state.next.json");
                File.WriteAllText(temporary, VisibleFixture(Interlocked.Increment(ref ticks)), new UTF8Encoding(false));
                File.Move(temporary, Path.Combine(fixture, "state.json"), overwrite: true);
            }
            WriteTick();
            async Task UpdateFixtureAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                try { while (await timer.WaitForNextTickAsync(updates.Token)) WriteTick(); }
                catch (OperationCanceledException) when (updates.IsCancellationRequested) { }
            }
            updater = UpdateFixtureAsync();
            window = new DesktopWindow(fixture, enableTerminal: false);
            var dashboard = Field<WebView2>(window, "dashboard");
            var terminal = Field<WebView2>(window, "terminalView");
            var status = Field<System.Windows.Controls.TextBlock>(window, "status");
            int loaded = 0;
            window.Loaded += (_, _) => loaded++;
            stage.Set("visible-operator-confirmation-pending");
            ShowVisibleAfterConfirmation(ConfirmVisibleStart, () =>
            {
                Console.Error.WriteLine("VISIBLE_OPERATOR_GATE: confirmed; showing test window once");
                window.Show();
            });
            nint hwnd = new WindowInteropHelper(window).Handle;
            await ObserveStartupAsync(window, dashboard, status, stage, () => loaded, TimeSpan.FromSeconds(30));
            Check(!status.Text.StartsWith("Startup failed:"), status.Text);
            Check(loaded == 1 && terminal.CoreWebView2 is null, "visible fixture startup or disabled Terminal invariant failed");
            AssertHardened(dashboard.CoreWebView2, terminal: false);
            AssertProfile(dashboard, Path.Combine(cache, "dashboard"));
            string origin = Field<string>(window, "mainOrigin");
            Check(LoopbackOrigin(origin) && new Uri(origin).Port != 8777, "visible fixture used an invalid/public port");
            Check(await StatusAsync(origin + "/", null, null) == System.Net.HttpStatusCode.Forbidden,
                "visible fixture accepted an unauthenticated page request");
            var navigation = new NavigationJournal();
            dashboard.CoreWebView2.NavigationStarting += (_, e) => navigation.Starting(e.Uri, e.NavigationId);
            dashboard.CoreWebView2.NavigationCompleted += (_, e) => navigation.Completed(e.NavigationId, e.IsSuccess);
            await NavigateAsync(dashboard, navigation, origin + "/observatory", TimeSpan.FromSeconds(15));
            await AssertDocumentAsync(dashboard, "/observatory", "Observatory", expectedTerminalLinks: 0);
            stage.Set("visible-observatory-ready");
            // One authorized activation at readiness; never reclaim focus during settling or sampling.
            bool activated = window.Activate(), foregroundRequested = SetForegroundWindow(hwnd);
            bool initialFocus = dashboard.Focus();
            Console.Error.WriteLine("VISIBLE_INITIAL_FOCUS: " + JsonSerializer.Serialize(new { activated, foreground_requested = foregroundRequested, requested_and_succeeded = initialFocus }));
            using var motionOverrideDocument = JsonDocument.Parse(await dashboard.CoreWebView2.ExecuteScriptAsync(EnableVisibleMotion).WaitAsync(TimeSpan.FromSeconds(2)));
            var motionOverride = motionOverrideDocument.RootElement.Clone();
            Console.Error.WriteLine("VISIBLE_MOTION_OVERRIDE: " + motionOverride.GetRawText());
            stage.Set("visible-readiness-pending");
            double readinessDeadline = Math.Min(runClock.Elapsed.TotalSeconds + 8, 25); // Reserve 15 seconds sampling plus cleanup within the outer 60-second cap.
            JsonElement readiness;
            while (true)
            {
                var browserReady = await EvaluateVisibleScriptAsync(dashboard, navigation, "readiness", ReadVisibleEligibility);
                using var boundary = JsonDocument.Parse(VisibleBoundaryJson(VisibleWindowState(hwnd), browserReady));
                readiness = boundary.RootElement.Clone();
                if (runClock.Elapsed.TotalSeconds >= readinessDeadline)
                {
                    Console.Error.WriteLine("VISIBLE_READINESS_FAILED: " + readiness.GetRawText());
                    throw new TimeoutException("visible readiness deadline expired before sampling: " + readiness.GetRawText());
                }
                if (readiness.GetProperty("readiness_failures").GetArrayLength() == 0) break;
                await Task.Delay(100);
            }
            Console.Error.WriteLine("VISIBLE_READINESS_PASSED: " + readiness.GetRawText());
            stage.Set("visible-settling");
            await Task.Delay(TimeSpan.FromSeconds(5));

            var browserSettled = await EvaluateVisibleScriptAsync(dashboard, navigation, "post-settle", ReadVisibleEligibility);
            string settled = VisibleBoundaryJson(VisibleWindowState(hwnd), browserSettled);
            Console.Error.WriteLine("VISIBLE_SETTLED: " + settled);
            Check(VisibleReadinessFailures(VisibleWindowState(hwnd), browserSettled).Length == 0,
                "visible readiness lost during settle; no sample taken: " + settled);

            var lifetime = Field<SafeJob>(window, "lifetime");
            int[] jobIds = lifetime.AssociatedProcessIds.Order().ToArray();
            foreach (int id in jobIds) Hold(held, id, "lifetime-job");
            Hold(held, Field<OwnedProcess>(window, "service").ProcessId, "python-service");
            CaptureWebViewProcesses(held, dashboard.CoreWebView2, "dashboard");
            int[] webIds = WebViewProcessIds(dashboard.CoreWebView2);
            var measured = held.Values.Append(host).ToArray();
            Check(measured.All(p => p.IsSameProcess), "visible sample had a stale process identity");
            string browserVersion = dashboard.CoreWebView2.Environment.BrowserVersionString;
            var browserStart = await EvaluateVisibleScriptAsync(dashboard, navigation, "sample-start", StartVisibleProbe);
            var nativeStart = VisibleWindowState(hwnd);
            string startBoundary = VisibleBoundaryJson(nativeStart, browserStart);
            Console.Error.WriteLine("VISIBLE_SAMPLE_START: " + startBoundary);
            Check(VisibleReadinessFailures(nativeStart, browserStart).Length == 0, "visible sample-start probe unavailable or not ready: " + startBoundary);
            stage.Set("visible-sample-started");
            var before = CpuSnapshot(measured);
            bool unobscured = nativeStart.unobscured;
            int windowChecks = 1;
            int firstTick = ticks;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(100);
                unobscured &= VisibleWindowState(hwnd).unobscured;
                windowChecks++;
            }
            clock.Stop();
            var nativeEnd = VisibleWindowState(hwnd);
            foreach (var process in measured) process.Refresh();
            bool stable = measured.All(p => p.IsSameProcess) && jobIds.SequenceEqual(lifetime.AssociatedProcessIds.Order()) &&
                webIds.SequenceEqual(WebViewProcessIds(dashboard.CoreWebView2));
            Console.Error.WriteLine("VISIBLE_SAMPLE_IDENTITIES: " + JsonSerializer.Serialize(new
            {
                stable, job_before = jobIds, job_after = lifetime.AssociatedProcessIds.Order().ToArray(),
                webview_before = webIds, webview_after = WebViewProcessIds(dashboard.CoreWebView2),
                retained = measured.Select(p => new { p.Id, p.StartTime, p.Roles, p.IsSameProcess })
            }));
            Check(stable, "visible sample process membership changed");
            var after = CpuSnapshot(measured);
            ProcessSample[] rows = measured.Select(p => new ProcessSample(p.Name, p.Id, p.StartTime.ToString("O"), p.Roles.Order().ToArray(),
                after[p.Id] - before[p.Id], 100 * (after[p.Id] - before[p.Id]) / clock.Elapsed.TotalSeconds / Environment.ProcessorCount,
                p.WorkingSet, p.PrivateBytes)).OrderBy(p => p.pid).ToArray();
            JsonElement heartbeat = await EvaluateVisibleScriptAsync(dashboard, navigation, "sample-end", "window.__foundryVisibleProbe.stop()");
            JsonElement browserEnd = heartbeat.ValueKind == JsonValueKind.Object && heartbeat.TryGetProperty("end", out var end)
                ? end : JsonSerializer.SerializeToElement<object?>(null);
            string endBoundary = VisibleBoundaryJson(nativeEnd, browserEnd);
            Console.Error.WriteLine("VISIBLE_SAMPLE_END: " + endBoundary);
            Check(!VisibleReadinessFailures(nativeEnd, browserEnd).Contains("browser_probe_unavailable"),
                "visible sample-end probe unavailable: " + endBoundary);
            stage.Set("visible-sample-captured");
            bool valid = unobscured && !heartbeat.GetProperty("focus_lost").GetBoolean() &&
                !heartbeat.GetProperty("inactive").GetBoolean() && heartbeat.GetProperty("agents").GetInt32() == 24 &&
                heartbeat.GetProperty("draw_heartbeat").GetProperty("count").GetInt32() > 0 && ticks - firstTick >= 9;
            bool memoryPass = VisibleMemoryPass(rows.Sum(p => p.working_set_bytes), rows.Sum(p => p.private_bytes));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema_version = 1, mode = "visible-observatory", measured_at = DateTimeOffset.UtcNow,
                bundle_manifest_sha256 = Convert.ToHexString(SHA256.HashData(
                    System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentFoundry.bundle.json")!)).ToLowerInvariant(),
                environment = new { os = RuntimeInformation.OSDescription, dotnet = RuntimeInformation.FrameworkDescription,
                    webview = browserVersion, logical_processors = Environment.ProcessorCount },
                settle_seconds = 5, sample_seconds = clock.Elapsed.TotalSeconds, synthetic_agents = 24, synthetic_projects = 4,
                fixture_updates_during_sample = ticks - firstTick, window_checks = windowChecks, continuously_unoccluded_samples = unobscured,
                native_sample_start = nativeStart, native_sample_end = nativeEnd,
                readiness, motion_override = motionOverride,
                active_measurement_valid = valid, memory_budget_pass = memoryPass,
                memory_budgets = new { working_set_strictly_below_bytes = VisibleWorkingSetLimit, private_commit_strictly_below_bytes = VisiblePrivateCommitLimit },
                cpu_seconds = rows.Sum(p => p.cpu_seconds), cpu_percent_one_core = 100 * rows.Sum(p => p.cpu_seconds) / clock.Elapsed.TotalSeconds,
                working_set_bytes = rows.Sum(p => p.working_set_bytes), private_bytes = rows.Sum(p => p.private_bytes), processes = rows,
                browser_scheduling = heartbeat, gpu_engine_utilization = "not measured; per-GPU-process CPU and memory are included, not GPU utilization",
                user_notes = (string?)null, physical_presentation_fps_measured = false, cleanup_pending = true,
                terminal_enabled = false, real_session_actions = 0, public_port_8777_used = false
            }));
            Check(valid, "visible active measurement was invalid: focus, occlusion, animation, fixture count or updates");
            Check(memoryPass, "visible Observatory memory reached its active-scene ceiling");
        }
        catch (Exception error)
        {
            primary = error; // Preserve the original exception before any diagnostic or cleanup operation.
            Console.Error.WriteLine("VISIBLE_PRIMARY_FAILED: stage=" + stage.Current + ": " + error);
            try { Console.Error.WriteLine("VISIBLE_FAILURE_IDENTITIES: " + JsonSerializer.Serialize(FailureIdentities(window))); }
            catch (Exception diagnostic) { Console.Error.WriteLine("VISIBLE_DIAGNOSTIC_FAILED: " + diagnostic); }
        }
        finally
        {
            updates.Cancel();
            await Cleanup("stop synthetic updates", () => updater.WaitAsync(TimeSpan.FromSeconds(2)));
            if (window is not null)
            {
                await Cleanup("retain current owned identities", () =>
                {
                    foreach (int id in Field<SafeJob>(window, "lifetime").AssociatedProcessIds) Hold(held, id, "lifetime-job");
                    return Task.CompletedTask;
                });
                foreach (string field in new[] { "dashboard", "terminalView" })
                    await Cleanup("retain " + field + " identities", () =>
                    {
                        var core = Field<WebView2>(window, field).CoreWebView2;
                        if (core is not null) CaptureWebViewProcesses(held, core, field);
                        return Task.CompletedTask;
                    });
                await Cleanup("stop owned services", () => InvokeTask(window, "StopOwnedAsync").WaitAsync(TimeSpan.FromSeconds(5)));
                // Dispose both controllers even if normal shutdown failed. CoreWebView2Environment is not IDisposable;
                // controller disposal releases its browser ownership, which the retained exit wait verifies below.
                foreach (string field in new[] { "dashboard", "terminalView" })
                    await Cleanup("dispose " + field, () => { Field<WebView2>(window, field).Dispose(); return Task.CompletedTask; });
                await Cleanup("verify empty lifetime Job", async () =>
                {
                    var lifetime = Field<SafeJob>(window, "lifetime");
                    await WaitUntilAsync(() => lifetime.AssociatedProcessIds.Count == 0, TimeSpan.FromSeconds(2), "visible fixture Job was not empty");
                    ownedProcessesAfterStop = 0;
                });
                await Cleanup("close test window", () => { window.Close(); return Task.CompletedTask; });
            }
            await Cleanup("wait for retained child exits", async () =>
            {
                await WaitUntilAsync(() => held.Values.All(p => p.HasExited), TimeSpan.FromSeconds(10), "visible fixture child remained after close");
                childrenExited = true;
            });
            foreach (var process in held.Values)
                await Cleanup("release retained process handle " + process.Id, () => { process.Dispose(); return Task.CompletedTask; });
            if (childrenExited)
            {
                await Cleanup("delete exact generated WebView cache", () => DeleteVisibleGeneratedAsync(cache,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentFoundry", "WebView"), Path.GetFileName(cache)));
                await Cleanup("delete exact generated fixture", () => DeleteVisibleGeneratedAsync(fixture, Path.GetTempPath(), Path.GetFileName(fixture)));
            }
            Console.Error.WriteLine("VISIBLE_CLEANUP_RESULT: " + JsonSerializer.Serialize(new
            {
                primary_failure = primary?.ToString(), cleanup_failures = cleanupErrors.Select(e => e.ToString()),
                owned_processes_after_stop = ownedProcessesAfterStop, retained_children_exited = childrenExited,
                fixture_removed = !Directory.Exists(fixture), cache_removed = !Directory.Exists(cache)
            }));
        }
        if (CombineVisibleErrors(primary, cleanupErrors) is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static bool VisibleMemoryPass(long workingSetBytes, long privateBytes) =>
        workingSetBytes < VisibleWorkingSetLimit && privateBytes < VisiblePrivateCommitLimit;

    static NativeVisibilityState VisibleWindowState(nint hwnd)
    {
        bool foreground = GetForegroundWindow() == hwnd, visible = IsWindowVisible(hwnd), minimized = IsIconic(hwnd);
        if (!GetWindowRect(hwnd, out var target)) return new(foreground, visible, minimized, "target_bounds_unavailable");
        nint above = GetTopWindow(0);
        for (int i = 0; above != 0 && i < 512; i++, above = GetWindow(above, 2)) // GW_HWNDNEXT
        {
            if (above == hwnd) return new(foreground, visible, minimized, "clear");
            if (!IsWindowVisible(above) || IsIconic(above)) continue;
            if (DwmGetWindowAttribute(above, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (GetWindowRect(above, out var bounds) && !OffDesktop(bounds, target)) return new(foreground, visible, minimized, "intersecting_window");
        }
        return new(foreground, visible, minimized, "z_order_incomplete");
    }

    const string ReadVisibleContext = """
    ({url:location.origin+location.pathname,ready:document.readyState,marker:window.__foundryVisibleContext??=crypto.randomUUID()})
    """;

    const string ReadVisibleEligibility = """
    ({document_has_focus:document.hasFocus(),document_hidden:document.hidden,paused,reduced_motion:reducedMotion.matches,motion_active:motionActive()})
    """;

    const string EnableVisibleMotion = """
    (()=>{
      const eligibility=()=>({document_has_focus:document.hasFocus(),document_hidden:document.hidden,paused,reduced_motion:reducedMotion.matches,motion_active:motionActive()});
      const before=eligibility();paused=false;motionLabel();paint();
      return {scope:'synthetic-page-only',before,after:eligibility()};
    })()
    """;

    const string StartVisibleProbe = """
    (()=>{
      const intervals=[],draws=[],longTasks=[];let last=performance.now(),lastDraw=renderedAt,id=0;
      const eligibility=()=>({document_has_focus:document.hasFocus(),document_hidden:document.hidden,paused,reduced_motion:reducedMotion.matches,motion_active:motionActive()});
      const start=eligibility();
      let focusLost=!document.hasFocus()||document.hidden,inactive=false;
      const lose=()=>{focusLost=true};window.addEventListener('blur',lose);
      const observer=typeof PerformanceObserver==='function'?new PerformanceObserver(list=>{for(const e of list.getEntries())if(longTasks.length<1000)longTasks.push(e.duration)}):null;
      try{observer?.observe({type:'longtask',buffered:false})}catch{}
      function frameProbe(now){if(intervals.length<4096)intervals.push(now-last);last=now;
        if(renderedAt!==lastDraw){if(draws.length<4096)draws.push(renderedAt-lastDraw);lastDraw=renderedAt}
        focusLost ||= !document.hasFocus()||document.hidden;inactive ||= !motionActive();id=requestAnimationFrame(frameProbe)}
      id=requestAnimationFrame(frameProbe);
      const summary=a=>{const sorted=a.slice().sort((a,b)=>a-b);return {count:a.length,mean_ms:a.length?a.reduce((x,y)=>x+y,0)/a.length:null,p95_ms:sorted.length?sorted[Math.ceil(sorted.length*.95)-1]:null,max_ms:sorted.length?sorted[sorted.length-1]:null}};
      window.__foundryVisibleProbe={stop(){cancelAnimationFrame(id);observer?.disconnect();window.removeEventListener('blur',lose);
        return {start,end:eligibility(),raf:summary(intervals),draw_heartbeat:summary(draws),long_tasks:summary(longTasks),focus_lost:focusLost,inactive,
          agents:workers.size,viewport:{width:canvas.clientWidth,height:canvas.clientHeight,dpr:devicePixelRatio}}}};
      return start;
    })()
    """;

    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetTopWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
}
