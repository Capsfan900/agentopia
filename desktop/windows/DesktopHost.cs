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
            if (!created) throw new InvalidOperationException("Agent Foundry already owns this data folder. Use its existing window.");
            var application = new Application();
            application.Run(new DesktopWindow(data, terminal));
            return 0;
        }
        catch (Exception error)
        {
            if (!args.Contains("--terminal-worker")) MessageBox.Show(error.Message, "Agent Foundry could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetDefaultDllDirectories(uint flags);
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
    VerifiedLaunch? bundle;
    OwnedProcess? service;
    TerminalServer? terminals;
    string mainOrigin = "";
    bool quiescing, closed, choosing, closing, openingTerminal;
    Task? stopTask;

    public DesktopWindow(string directory, bool enableTerminal)
    {
        dataDirectory = directory; terminalEnabled = enableTerminal;
        Title = "Agent Foundry"; Width = Math.Min(1500, SystemParameters.WorkArea.Width); Height = Math.Min(960, SystemParameters.WorkArea.Height);
        MinWidth = 800; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        panels.Children.Add(dashboard); terminalView.Visibility = Visibility.Collapsed; panels.Children.Add(terminalView);
        root.Children.Add(panels); Content = root;
        Loaded += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; http.Dispose(); lifetime.Dispose(); bundle?.Dispose(); };
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
            var save = new Microsoft.Win32.SaveFileDialog { Title = "Save reviewed Foundry Package", FileName = "agent-foundry-package.zip", DefaultExt = ".zip", Filter = "Foundry Package (*.zip)|*.zip", OverwritePrompt = true };
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
        core.ProcessFailed += (_, _) => { status.Visibility = Visibility.Visible; status.Text = "A page process stopped. Commands were not retried. Close and restart Foundry."; };
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
    void PanesChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (terminals is not null)
        {
            var ids = terminals.Panes.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in launches.Keys.Where(x => !ids.Contains(x)).ToArray()) launches.Remove(id);
        }
        if (!quiescing && terminalView.CoreWebView2 is not null) terminalView.CoreWebView2.PostWebMessageAsJson("{\"kind\":\"panesChanged\"}");
    });
    async Task WatchService(OwnedProcess owned)
    {
        await owned.WaitForExitAsync();
        if (!quiescing && !closed) await Dispatcher.InvokeAsync(async () =>
        {
            status.Visibility = Visibility.Visible; status.Text = "The owned service stopped. All terminal panes are being closed; restart Foundry explicitly.";
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
            "Quit Agent Foundry", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        closing = true;
        await StopOwnedAsync(); closed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    Task StopOwnedAsync() => stopTask ??= StopCoreAsync();
    async Task StopCoreAsync()
    {
        quiescing = true;
        try
        {
            service?.StandardInput.Dispose();
            await Task.WhenAll(terminals is null ? Task.CompletedTask : terminals.DisposeAsync().AsTask(),
                service is null ? Task.CompletedTask : service.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException) { }
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
