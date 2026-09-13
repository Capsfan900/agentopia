using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AgentFoundry.Desktop;

sealed class LaunchDialog : Window
{
    readonly JsonElement snapshot;
    readonly Dictionary<string, TerminalProfile> profiles;
    readonly string profileWarnings;
    readonly ComboBox profile = new(), mode = new(), session = new();
    readonly TextBox directory = new(), model = new();
    readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    readonly CheckBox consent = new() { Content = "I reviewed the settings and want to start this owned terminal." };
    public TerminalLaunch? Result { get; private set; }
    record SessionChoice(string Id, string Cwd, string Status, string Label) { public override string ToString() => Label; }

    public LaunchDialog(JsonElement state, TerminalLaunch? previous, string dataDirectory)
    {
        snapshot = state.Clone(); Title = "Review terminal launch"; Width = 720; Height = 710;
        profiles = TerminalProfiles.Load(dataDirectory, out var warnings).ToDictionary(x => x.Name, StringComparer.Ordinal);
        profileWarnings = warnings.Length == 0 ? "" : "\nUnavailable profiles:\n" + string.Join("\n", warnings.Select(x => "• " + x));
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        var panel = new StackPanel { Margin = new Thickness(18) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Launch settings — nothing starts until confirmed", FontSize = 18 });
        AddField(panel, "Profile", profile);
        foreach (var name in profiles.Keys) profile.Items.Add(name);
        profile.SelectedItem = previous?.ProfileName ?? (profile.Items.Contains("Codex") ? "Codex" : profile.Items.Cast<string>().FirstOrDefault());
        AddField(panel, "Action", mode); foreach (var name in new[] { "new", "resume", "fork" }) mode.Items.Add(name);
        mode.SelectedItem = previous?.Mode ?? "new";
        AddField(panel, "Existing session (resume or fork only)", session);
        if (snapshot.GetProperty("adapter").GetString() == "codex")
            foreach (var row in snapshot.GetProperty("sessions").EnumerateArray())
            {
                var id = row.GetProperty("id").GetString()!;
                try { TerminalBoundary.Id(id); } catch (ArgumentException) { continue; }
                var cwd = row.GetProperty("cwd").GetString() ?? "";
                var status = row.GetProperty("status").GetString() ?? "unknown";
                var choice = new SessionChoice(id, cwd, status, $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd))} · {status} · {id}");
                session.Items.Add(choice); if (id == previous?.SourceSessionId) session.SelectedItem = choice;
            }
        AddField(panel, "Project folder (local, explicitly reviewed)", directory);
        directory.Text = previous?.WorkingDirectory ?? "";
        var browse = new Button { Content = "Choose project folder…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        browse.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "Choose the project this terminal will work in", Multiselect = false }; if (picker.ShowDialog(this) == true) directory.Text = picker.FolderName; };
        panel.Children.Add(browse);
        AddField(panel, "Codex model (blank uses your existing configuration)", model);
        if (previous?.Arguments.Contains("--model") == true) model.Text = previous.Arguments[^1];
        panel.Children.Add(details); panel.Children.Add(consent);
        var launch = new Button { Content = "Confirm and launch", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        panel.Children.Add(launch);
        profile.SelectionChanged += (_, _) => UpdateReview(); mode.SelectionChanged += (_, _) => UpdateReview();
        session.SelectionChanged += (_, _) => { if (session.SelectedItem is SessionChoice selected) directory.Text = selected.Cwd; UpdateReview(); };
        directory.TextChanged += (_, _) => UpdateReview(); model.TextChanged += (_, _) => UpdateReview();
        launch.Click += (_, _) =>
        {
            try
            {
                if (consent.IsChecked != true) throw new InvalidOperationException("Review and acknowledge the launch settings first.");
                var candidate = Build();
                VerifySession(snapshot, candidate);
                using (VerifiedLaunch.Open(candidate)) { }
                Result = candidate; DialogResult = true;
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Launch settings need attention", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        UpdateReview();
    }

    void UpdateReview()
    {
        consent.IsChecked = false;
        var name = profile.SelectedItem as string ?? "";
        bool codex = name == "Codex";
        if (!codex) mode.SelectedItem = "new";
        mode.IsEnabled = codex; session.IsEnabled = codex && (string?)mode.SelectedItem != "new"; model.IsEnabled = codex;
        profiles.TryGetValue(name, out var selected);
        details.Text = $"Executable: {selected?.Executable}\nReviewed SHA-256: {selected?.Sha256 ?? "Unavailable"}\nProject: {directory.Text}\n" +
            (codex ? "Codex: workspace-write sandbox, on-request approvals. Other settings, trusted hooks and tools come from your existing Codex configuration. Paid provider usage and project changes may occur. No Agent Template is installed automatically.\n" : "This is a shell, not an AI agent. Commands can change your files.\n") +
            "Only native Job-associated processes are owned. WSL, elevated sessions and externally brokered services are not supported here. Terminal contents and keystrokes are not saved by Foundry. New/fork destination IDs remain unknown until independently reported." + profileWarnings;
    }

    TerminalLaunch Build()
    {
        var name = profile.SelectedItem as string ?? throw new InvalidOperationException("Choose an available profile.");
        var selectedProfile = profiles[name];
        var action = name == "Codex" ? mode.SelectedItem as string ?? "new" : "new";
        var cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Text));
        var selected = session.SelectedItem as SessionChoice;
        if (action != "new" && selected is null) throw new InvalidOperationException("Choose an actual recorded session to resume or fork.");
        var arguments = new List<string>();
        var environment = ChildEnvironment();
        if (name == "Codex")
        {
            if (snapshot.GetProperty("adapter").GetString() != "codex") throw new InvalidOperationException("Select the Codex adapter before launching Codex work.");
            if (action != "new") { arguments.Add(action); arguments.Add(selected!.Id); }
            arguments.AddRange(["-C", cwd, "--sandbox", "workspace-write", "--ask-for-approval", "on-request"]);
            if (!string.IsNullOrWhiteSpace(model.Text)) arguments.AddRange(["--model", model.Text.Trim()]);
            environment["CODEX_HOME"] = snapshot.GetProperty("source").GetString()!;
        }
        else arguments.AddRange(selectedProfile.Arguments);
        var executable = selectedProfile.Executable;
        var hash = selectedProfile.Sha256;
        return new(name, executable, hash, arguments.ToArray(), cwd, environment, action,
            action == "new" ? null : selected!.Id, action == "resume" ? selected!.Id : null);
    }

    public static void VerifySession(JsonElement snapshot, TerminalLaunch launch)
    {
        if (launch.SourceSessionId is null) return;
        if (snapshot.GetProperty("adapter").GetString() != "codex") throw new InvalidOperationException("Only recorded Codex sessions can be resumed or forked.");
        var matches = snapshot.GetProperty("sessions").EnumerateArray().Where(x => x.GetProperty("id").GetString() == launch.SourceSessionId).ToArray();
        if (matches.Length != 1 || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(matches[0].GetProperty("cwd").GetString()!)),
                launch.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected session or project changed. Review it again.");
        if (launch.Mode == "resume" && matches[0].GetProperty("status").GetString() is not ("idle" or "done" or "completed" or "failed"))
            throw new InvalidOperationException("That session may still be active. Use confirmed Send in Operations, or explicitly fork it.");
    }

    public static Dictionary<string, string> ChildEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "SystemRoot", "WINDIR", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) result[key] = value;
        result["PATH"] = string.Join(';', (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
            .Where(x => Path.IsPathFullyQualified(x) && !x.StartsWith(@"\\") && !x.Contains('%')).Distinct(StringComparer.OrdinalIgnoreCase));
        result["PATHEXT"] = ".COM;.EXE;.BAT;.CMD"; result["TERM"] = "xterm-256color"; result["COLORTERM"] = "truecolor";
        return result;
    }
    static void AddField(Panel panel, string text, Control control)
    {
        panel.Children.Add(new Label { Content = text, Target = control, Margin = new Thickness(0, 7, 0, 0) });
        System.Windows.Automation.AutomationProperties.SetName(control, text);
        panel.Children.Add(control);
    }
}
