global using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentFoundry.Desktop;

static class DesktopProgram
{
    internal static void ClearWebViewEnvironment()
    {
        foreach (var key in new[] { "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER" })
            Environment.SetEnvironmentVariable(key, null);
    }

    [STAThread]
    static int Main(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        if (!SetDefaultDllDirectories(0x1000)) return 1;
        try
        {
            Native.EnsureWindowsAndNonElevated();
            if (args.SequenceEqual(["--terminal-worker"])) return TerminalWorker.RunAsync().GetAwaiter().GetResult();
            ClearWebViewEnvironment();
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentFoundry");
            bool terminal = false;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--enable-terminal") terminal = true;
                else if (args[i] == "--data-dir" && i + 1 < args.Length) data = Path.GetFullPath(args[++i]);
                else throw new ArgumentException("Use --data-dir <folder> and optionally --enable-terminal (experimental).");
            using var instance = new Mutex(true, @"Local\AgentFoundry-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant()))), out bool created);
            if (!created) throw new InvalidOperationException("Agentopia already owns this data folder. Use its existing window.");
            var application = new Application();
            application.Run(new DesktopWindow(data, terminal));
            return 0;
        }
        catch (Exception error)
        {
            if (!args.Contains("--terminal-worker")) MessageBox.Show(error.Message, "Agentopia could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetDefaultDllDirectories(uint flags);
}

internal static class TerminalContextBridge
{
    const int MaxStateChars = 4 * 1024 * 1024;
    static readonly HashSet<string> Statuses = ["working", "tool", "thinking", "approval", "idle", "completed", "interrupted", "failed"];

    public static string? ExtractStateData(string line)
    {
        if (!line.StartsWith("data: ", StringComparison.Ordinal)) return null;
        string value = line[6..];
        if (value.Length > MaxStateChars) throw new InvalidDataException("Terminal context snapshot exceeds 4 MiB.");
        return value;
    }

    public static string Project(string snapshotJson, IReadOnlyDictionary<string, TerminalLaunch> launches)
    {
        using var document = JsonDocument.Parse(snapshotJson, new JsonDocumentOptions { MaxDepth = 24 });
        JsonElement root = document.RootElement;
        string adapter = Text(root, "adapter", 40), source = Text(root, "source", 1024);
        JsonElement[] sessions = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sessions", out JsonElement rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Take(10000).ToArray() : [];
        var panes = launches.Take(8).Select(pair => Context(pair.Key, pair.Value, adapter, source, sessions)).ToArray();
        return JsonSerializer.Serialize(new { kind = "sessionContext", panes });
    }

    static object Context(string paneId, TerminalLaunch launch, string adapter, string source, JsonElement[] sessions)
    {
        if (launch.ProfileName != "Codex") return new { paneId, relation = "unlinked" };
        string? id = launch.SessionId ?? launch.SourceSessionId;
        string relation = launch.SessionId is not null ? "current" : launch.SourceSessionId is not null ? "source" : "unavailable";
        if (id is null || adapter != "codex" || !launch.Environment.TryGetValue("CODEX_HOME", out string? enrolledSource) ||
            !SamePath(source, enrolledSource)) return new { paneId, relation = "unavailable" };
        JsonElement[] matches = sessions.Where(row => row.ValueKind == JsonValueKind.Object && Text(row, "id", 64) == id).Take(2).ToArray();
        if (matches.Length != 1 || !SamePath(Text(matches[0], "cwd", 1024), launch.WorkingDirectory))
            return new { paneId, relation = "unavailable" };
        JsonElement session = matches[0];
        string status = Text(session, "status", 40);
        if (!Statuses.Contains(status)) status = "idle";
        string workingOn = "", workingSource = "";
        if (session.TryGetProperty("working_on", out JsonElement working) && working.ValueKind == JsonValueKind.Object)
        {
            workingOn = Text(working, "summary", 260); workingSource = Text(working, "source", 120);
            if (Text(working, "status", 40) == "approval") status = "approval";
        }
        if (workingOn.Length == 0) workingOn = Text(session, "current_action", 260);
        bool approval = Bool(session, "approval_pending") || status == "approval";
        object handoff = Handoff(session);
        return new { paneId, relation, sessionId = id, workingOn, workingSource, status,
            turnStatus = Text(session, "turn_status", 80), approvalPending = approval, stale = Bool(session, "stale"),
            updated = Text(session, "updated", 80), handoff };
    }

    static object Handoff(JsonElement session)
    {
        if (!session.TryGetProperty("saved_handoff", out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            return new { state = "unavailable" };
        string state = Text(value, "state", 20);
        if (state == "none") return new { state };
        if (state != "saved") return new { state = "unavailable" };
        object? completion = null;
        if (value.TryGetProperty("completion", out JsonElement finished) && finished.ValueKind == JsonValueKind.Object)
        {
            string source = Text(finished, "source", 20);
            completion = new { completed = Bool(finished, "completed"), source,
                trusted = (source is "runtime" or "user") && Bool(finished, "trusted") };
        }
        return new { state, itemId = Text(value, "item_id", 200), revision = Hash(value, "revision"),
            capturedAt = Text(value, "captured_at", 80), archived = Bool(value, "archived"), completion };
    }

    static string Text(JsonElement value, string name, int limit)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out JsonElement item) || item.ValueKind != JsonValueKind.String)
            return "";
        string text = item.GetString()?.Trim() ?? "";
        return text.Length <= limit ? text : text[..limit];
    }
    static string Hash(JsonElement value, string name)
    {
        string result = Text(value, name, 64);
        return result.Length == 64 && result.All(char.IsAsciiHexDigit) ? result.ToLowerInvariant() : "";
    }
    static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement item) && item.ValueKind == JsonValueKind.True;
    static bool SamePath(string left, string right)
    {
        try { return left.Length > 0 && right.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

internal sealed class BoundedLineReader(Stream stream, int maximumBytes)
{
    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    readonly byte[] buffer = new byte[4096];
    int offset, count;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream(Math.Min(maximumBytes, buffer.Length));
        while (true)
        {
            if (offset == count)
            {
                count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                offset = 0;
                if (count == 0)
                {
                    if (line.Length == 0) return null;
                    throw new EndOfStreamException("SSE stream ended inside a line.");
                }
            }
            int newline = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
            int take = (newline < 0 ? count : newline) - offset;
            if (line.Length + take > maximumBytes) throw new InvalidDataException("SSE line exceeds its byte limit.");
            line.Write(buffer, offset, take);
            offset += take;
            if (newline < 0) continue;
            offset++;
            if (line.Length > 0 && line.GetBuffer()[line.Length - 1] == '\r') line.SetLength(line.Length - 1);
            try { return StrictUtf8.GetString(line.GetBuffer(), 0, checked((int)line.Length)); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("SSE line is not valid UTF-8.", error); }
        }
    }
}

sealed class DesktopWindow : Window
{
    internal static string WebViewDataDirectory(string directory, bool terminal)
    {
        var dataId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..16];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentFoundry", "WebView", dataId, terminal ? "terminal" : "dashboard");
    }

    readonly string dataDirectory;
    readonly bool terminalEnabled;
    readonly SafeJob lifetime = new();
    readonly WebView2 dashboard = new(), terminalView = new();
    readonly Grid panels = new();
    readonly TextBlock status = new() { Margin = new Thickness(10), Text = "Starting private service…" };
    readonly Dictionary<string, TerminalLaunch> launches = new();
    readonly string mainKey = Secret(), terminalKey = Secret();
    readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    readonly CancellationTokenSource contextUpdates = new();
    readonly object contextGate = new();
    VerifiedLaunch? bundle;
    OwnedProcess? service;
    TerminalServer? terminals;
    string mainOrigin = "";
    string? lastStateJson, pendingContextPayload;
    Task? contextTask;
    bool contextDeliveryPending;
    bool quiescing, closed, choosing, closing, openingTerminal;
    Task? stopTask;

    public DesktopWindow(string directory, bool enableTerminal)
    {
        dataDirectory = directory; terminalEnabled = enableTerminal;
        Title = "Agentopia"; Width = Math.Min(1500, SystemParameters.WorkArea.Width); Height = Math.Min(960, SystemParameters.WorkArea.Height);
        MinWidth = 800; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        panels.Children.Add(dashboard); terminalView.Visibility = Visibility.Collapsed; panels.Children.Add(terminalView);
        root.Children.Add(panels); Content = root;
        Loaded += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; contextUpdates.Dispose(); http.Dispose(); lifetime.Dispose(); bundle?.Dispose(); };
    }

    async Task StartAsync()
    {
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentFoundry.bundle.json")
                ?? throw new InvalidOperationException("Private runtime bundle is missing. Run desktop/windows/package.py and rebuild before launching.");
            using var manifest = JsonDocument.Parse(resource);
            if (manifest.RootElement.GetProperty("schema_version").GetInt32() != 1) throw new InvalidOperationException("Unsupported runtime bundle");
            var files = manifest.RootElement.GetProperty("files").Deserialize<Dictionary<string, string>>()!;
            var verified = await Task.Run(() => VerifiedLaunch.OpenBundle(AppContext.BaseDirectory, files));
            if (quiescing) { verified.Dispose(); return; }
            bundle = verified;
            await RefuseExistingWriter();
            if (quiescing) return;
            var appPath = Path.Combine(AppContext.BaseDirectory, "app");
            var python = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
            service = OwnedProcess.Start(python, ["-I", "-S", "-B", "-u", Path.Combine(appPath, "monitor.py"), "--desktop", "--data-dir", dataDirectory],
                appPath, LaunchDialog.ChildEnvironment(), lifetime);
            await service.StandardInput.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { key = mainKey }) + "\n"));
            await service.StandardInput.FlushAsync();
            var readyBytes = await ReadLine(service.StandardOutput, 4096).WaitAsync(TimeSpan.FromSeconds(10));
            using var ready = TerminalBoundary.Parse(readyBytes, ["port", "pid"]);
            int port = ready.RootElement.GetProperty("port").GetInt32();
            if (port is < 1024 or > 65535 || ready.RootElement.GetProperty("pid").GetInt32() != service.ProcessId)
                throw new InvalidOperationException("Private service identity did not match its owned process");
            mainOrigin = "http://127.0.0.1:" + port;
            http.BaseAddress = new Uri(mainOrigin); http.DefaultRequestHeaders.Add("X-Foundry-Desktop", mainKey);
            _ = WatchService(service);
            _ = DrainServiceOutput(service.StandardOutput);
            if (terminalEnabled)
            {
                var self = Environment.ProcessPath!;
                var created = await TerminalServer.StartAsync(Path.Combine(AppContext.BaseDirectory, "terminal"), terminalKey, self, Array.Empty<string>(), lifetime);
                if (quiescing) { await created.DisposeAsync(); return; }
                terminals = created;
                terminals.PanesChanged += PanesChanged;
            }
            await PrepareView(dashboard, mainOrigin, mainKey, terminal: false);
            if (quiescing) return;
            dashboard.CoreWebView2.Navigate(mainOrigin + "/");
            status.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            if (!closed) { status.Visibility = Visibility.Visible; status.Text = "Startup failed: " + error.Message + " Close this window and use browser mode if needed."; }
            await StopOwnedAsync();
        }
    }

    async Task RefuseExistingWriter()
    {
        int port = 8777;
        var settings = Path.Combine(dataDirectory, "settings.json");
        if (File.Exists(settings))
        {
            if (new FileInfo(settings).Length > 16384) throw new IOException("Settings file is too large");
            using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(settings));
            port = saved.RootElement.GetProperty("config").GetProperty("port").GetInt32();
        }
        using var probe = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(1), MaxResponseContentBufferSize = 65536 };
        try
        {
            using var response = await probe.GetAsync($"http://127.0.0.1:{port}/api/bootstrap");
            if (!response.IsSuccessStatusCode) return;
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            if (document.RootElement.TryGetProperty("data_dir", out var value) &&
                string.Equals(Path.GetFullPath(value.GetString()!), Path.GetFullPath(dataDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A browser-mode service already uses this data folder. Stop it yourself before starting the desktop owner; it will not be adopted or interrupted.");
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (JsonException) { throw new InvalidOperationException("Could not verify the existing browser service. Stop that service before using this data folder."); }
    }

    async Task PrepareView(WebView2 view, string origin, string key, bool terminal)
    {
        // Independent profile/storage for Terminal; no promise of OS process isolation between WebViews.
        var cache = WebViewDataDirectory(dataDirectory, terminal);
        var environment = await CoreWebView2Environment.CreateAsync(null, cache, new CoreWebView2EnvironmentOptions
            { AdditionalBrowserArguments = "--disable-background-networking --disable-sync" });
        if (quiescing) return;
        view.CreationProperties = new CoreWebView2CreationProperties { IsInPrivateModeEnabled = true };
        await view.EnsureCoreWebView2Async(environment);
        if (quiescing) { view.Dispose(); return; }
        var core = view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = terminal;
        core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationStarting += async (_, e) =>
        {
            e.Cancel = quiescing || !SameOrigin(e.Uri, origin);
            // Routes stay in the web navigation. The host only switches the two owned origins;
            // no shared storage, process-launch bridge, or second navigation model is introduced.
            var other = terminal ? dashboard : terminalView;
            var otherOrigin = terminal ? mainOrigin : terminals?.Origin;
            if (e.Cancel && !quiescing && otherOrigin is not null &&
                TerminalBoundary.OwnedUri(e.Uri, otherOrigin, terminal: !terminal))
            {
                try
                {
                    if (other == terminalView && other.CoreWebView2 is null)
                    {
                        if (openingTerminal) return;
                        openingTerminal = true;
                        try { await PrepareView(terminalView, otherOrigin, terminalKey, terminal: true); }
                        finally { openingTerminal = false; }
                    }
                    if (quiescing || other.CoreWebView2 is null) return;
                    view.Visibility = Visibility.Collapsed; other.Visibility = Visibility.Visible;
                    if (other.Source?.AbsoluteUri != e.Uri) other.CoreWebView2.Navigate(e.Uri);
                    other.Focus();
                }
                catch (Exception error) { if (!quiescing) { status.Visibility = Visibility.Visible; status.Text = "Terminal page could not open: " + error.Message; } }
            }
        };
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            if (terminal || quiescing || !e.DownloadOperation.Uri.StartsWith("blob:" + origin + "/", StringComparison.Ordinal)) return;
            var save = new Microsoft.Win32.SaveFileDialog { Title = "Save reviewed Agentopia Package", FileName = "agentopia-package.zip", DefaultExt = ".zip", Filter = "Agentopia Package (*.zip)|*.zip", OverwritePrompt = true };
            if (save.ShowDialog(this) == true) { e.ResultFilePath = save.FileName; e.Handled = true; e.Cancel = false; }
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (!quiescing && SameOrigin(uri, origin))
                e.Request.Headers.SetHeader(terminal ? "X-Foundry-Terminal" : "X-Foundry-Desktop", key);
            else if (!(!quiescing && (terminal ? uri == origin.Replace("http://", "ws://") + "/connect" : uri.StartsWith("blob:" + origin + "/", StringComparison.Ordinal))))
                e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain\r\nCache-Control: no-store");
        };
        core.ProcessFailed += (_, _) => { status.Visibility = Visibility.Visible; status.Text = "A page process stopped. Commands were not retried. Close and restart Agentopia."; };
        if (terminal) core.DOMContentLoaded += (_, _) => { StartContextReader(); _ = RefreshContextProjectionAsync(); };
        if (terminal || terminals is not null)
        {
            string ownedOrigin = JsonSerializer.Serialize(origin), destination = JsonSerializer.Serialize(terminal ? mainOrigin : terminals!.Origin);
            string navigation = terminal
                ? "document.querySelectorAll('.app-nav a').forEach(a=>a.href=destination+new URL(a.href).pathname);"
                : "const nav=document.querySelector('.app-nav');if(nav){const a=document.createElement('a');a.href=destination+'/';a.textContent='Terminal';nav.insertBefore(a,nav.querySelector('button'));}";
            await core.AddScriptToExecuteOnDocumentCreatedAsync("document.addEventListener('DOMContentLoaded',()=>{if(location.origin!==" + ownedOrigin + ")return;const destination=" + destination + ";" + navigation + "});");
        }
        if (terminal) core.WebMessageReceived += async (_, e) =>
        {
            if (quiescing || !SameOrigin(e.Source, origin) || choosing) return;
            try
            {
                var raw = e.WebMessageAsJson;
                if (raw.Length > 4096) return;
                using var message = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 4 });
                if (message.RootElement.ValueKind != JsonValueKind.Object) return;
                var fields = message.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
                if (!message.RootElement.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String) return;
                var kind = kindValue.GetString();
                if (kind == "chooseLaunch" && fields.SequenceEqual(["kind"])) await ChooseLaunch();
                else if (kind == "refreshContext" && fields.SequenceEqual(["kind"])) { StartContextReader(); await RefreshContextProjectionAsync(); }
                else if (kind == "restartPane" && fields.Length == 2 && fields.Distinct().Count() == 2 && fields.Contains("paneId"))
                {
                    var id = TerminalBoundary.Id(message.RootElement.GetProperty("paneId").GetString()!);
                    if (launches.TryGetValue(id, out var prior)) await ChooseLaunch(prior, id);
                }
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { status.Text = "Terminal request rejected. Use its explicit launch controls."; }
        };
    }

    async Task ChooseLaunch(TerminalLaunch? previous = null, string? replace = null)
    {
        if (quiescing || terminals is null || choosing) return;
        choosing = true;
        try
        {
            using var snapshot = await ReadSnapshot();
            var dialog = new LaunchDialog(snapshot.RootElement, previous, dataDirectory) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is null || quiescing) return;
            var launch = dialog.Result;
            // Re-resolve session immediately before creation, not from a WebView's claim or old dialog snapshot.
            if (launch.SourceSessionId is not null)
            {
                using var fresh = await ReadSnapshot();
                LaunchDialog.VerifySession(fresh.RootElement, launch);
            }
            using (VerifiedLaunch.Open(launch)) { }
            if (replace is not null) { await terminals.ClosePaneAsync(replace); launches.Remove(replace); }
            if (quiescing) return;
            var id = await terminals.LaunchAsync(launch);
            launches[id] = launch;
        }
        catch (Exception error) { if (!quiescing) MessageBox.Show(this, error.Message, "Terminal was not started", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { choosing = false; }
    }

    async Task<JsonDocument> ReadSnapshot()
    {
        using var response = await http.GetAsync("/api/state");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
    }

    void StartContextReader()
    {
        if (quiescing || contextUpdates.IsCancellationRequested || contextTask is { IsCompleted: false }) return;
        contextTask = WatchContextEvents(contextUpdates.Token);
    }

    async Task WatchContextEvents(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var reader = new BoundedLineReader(stream, 4 * 1024 * 1024 + 6);
            while (!cancellationToken.IsCancellationRequested &&
                await reader.ReadLineAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false) is string line)
                if (TerminalContextBridge.ExtractStateData(line) is string state) await ProjectContextAsync(state).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested) await ProjectContextAsync("{}").ConfigureAwait(false);
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested || error is HttpRequestException or IOException or JsonException or TaskCanceledException or TimeoutException)
        {
            if (!cancellationToken.IsCancellationRequested) await ProjectContextAsync("{}").ConfigureAwait(false);
        }
    }

    async Task RefreshContextProjectionAsync()
    {
        string? state;
        lock (contextGate) state = lastStateJson;
        if (state is null)
        {
            try { using var snapshot = await ReadSnapshot(); state = snapshot.RootElement.GetRawText(); }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or TaskCanceledException) { state = "{}"; }
        }
        await ProjectContextAsync(state);
    }

    async Task ProjectContextAsync(string state)
    {
        Dictionary<string, TerminalLaunch> current = Dispatcher.CheckAccess()
            ? launches.ToDictionary()
            : await Dispatcher.InvokeAsync(() => launches.ToDictionary()).Task.ConfigureAwait(false);
        string payload = TerminalContextBridge.Project(state, current);
        lock (contextGate)
        {
            lastStateJson = state == "{}" ? null : state;
            pendingContextPayload = payload;
            if (contextDeliveryPending) return;
            contextDeliveryPending = true;
        }
        _ = Dispatcher.BeginInvoke(DeliverContext);
    }

    void DeliverContext()
    {
        string? payload;
        lock (contextGate)
        {
            payload = pendingContextPayload; pendingContextPayload = null; contextDeliveryPending = false;
        }
        if (!quiescing && payload is not null && terminalView.CoreWebView2 is not null)
            terminalView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    void PanesChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (terminals is not null)
        {
            var ids = terminals.Panes.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in launches.Keys.Where(x => !ids.Contains(x)).ToArray()) launches.Remove(id);
        }
        if (!quiescing && terminalView.CoreWebView2 is not null) terminalView.CoreWebView2.PostWebMessageAsJson("{\"kind\":\"panesChanged\"}");
        if (!quiescing) _ = RefreshContextProjectionAsync();
    });
    async Task WatchService(OwnedProcess owned)
    {
        await owned.WaitForExitAsync();
        if (!quiescing && !closed) await Dispatcher.InvokeAsync(async () =>
        {
            status.Visibility = Visibility.Visible; status.Text = "The owned service stopped. All terminal panes are being closed; restart Agentopia explicitly.";
            await StopOwnedAsync();
        });
    }
    static async Task DrainServiceOutput(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        if (launches.Count > 0 && MessageBox.Show(this, "Quit and stop all owned terminal processes? Externally brokered/elevated processes are outside this guarantee.",
            "Quit Agentopia", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        closing = true;
        await StopOwnedAsync(); closed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    Task StopOwnedAsync() => stopTask ??= StopCoreAsync();
    async Task StopCoreAsync()
    {
        quiescing = true;
        contextUpdates.Cancel();
        try
        {
            service?.StandardInput.Dispose();
            await Task.WhenAll(contextTask ?? Task.CompletedTask,
                terminals is null ? Task.CompletedTask : terminals.DisposeAsync().AsTask(),
                service is null ? Task.CompletedTask : service.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            lifetime.Terminate(); service?.Dispose(); service = null;
            dashboard.Dispose(); terminalView.Dispose(); launches.Clear();
        }
    }
    static bool SameOrigin(string value, string origin) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
        uri.UserInfo.Length == 0 && uri.GetLeftPart(UriPartial.Authority) == origin;
    static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    static async Task<byte[]> ReadLine(Stream stream, int limit)
    {
        using var bytes = new MemoryStream(); var next = new byte[1];
        while (bytes.Length < limit && await stream.ReadAsync(next) == 1)
        { if (next[0] == 10) return bytes.ToArray(); bytes.WriteByte(next[0]); }
        throw new IOException("Private service did not complete its bounded startup handshake");
    }
}
