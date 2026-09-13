using AgentFoundry.Desktop;
using System.Text;
using System.Text.Json;

static void Check(bool value, string name) { if (!value) throw new Exception(name); }
try
{
var pane = Guid.NewGuid().ToString();
var generation = Guid.NewGuid().ToString();
var tickets = new AttachmentTickets();
var now = DateTimeOffset.UtcNow;
var token = tickets.Mint(pane, generation, now);
var consumed = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => tickets.Consume(token, pane, generation, now))));
Check(consumed.Count(x => x) == 1, "A ticket must be consumed atomically once");
token = tickets.Mint(pane, generation, now);
Check(!tickets.Consume(token, Guid.NewGuid().ToString(), generation, now), "Wrong pane cannot attach");
token = tickets.Mint(pane, generation, now);
Check(!tickets.Consume(token, pane, Guid.NewGuid().ToString(), now), "Wrong generation cannot attach");
token = tickets.Mint(pane, generation, now);
Check(!tickets.Consume(token, pane, generation, now.AddSeconds(21)), "Expired ticket cannot attach");
tickets.RevokeAll();
try { tickets.Mint(pane, generation, now); throw new Exception("Mint after shutdown succeeded"); }
catch (InvalidOperationException) { }

const string authority = "127.0.0.1:45678";
var secret = new string('a', 64);
Check(TerminalBoundary.Http([authority], [], [secret], authority, secret, "GET"), "Owned navigation allowed");
Check(TerminalBoundary.Http([authority], ["http://" + authority], [secret], authority, secret, "POST"), "Owned POST allowed");
foreach (var origin in new[] { Array.Empty<string>(), new[] { "null" }, new[] { "http://evil.example" }, new[] { "http://" + authority, "http://" + authority } })
    Check(!TerminalBoundary.Http([authority], origin, [secret], authority, secret, "POST"), "POST origin rejected");
Check(!TerminalBoundary.Http([authority, authority], [], [secret], authority, secret, "GET"), "Duplicate Host rejected");
Check(!TerminalBoundary.Http([authority], [], [secret, secret], authority, secret, "GET"), "Duplicate secret rejected");
Check(!TerminalBoundary.Http([authority], [], ["wrong"], authority, secret, "GET"), "Foreign secret rejected");
Check(!TerminalBoundary.WebSocket([authority], [], authority), "Missing WS Origin rejected before upgrade");
Check(TerminalBoundary.WebSocket([authority], ["http://" + authority], authority), "Exact WS Origin allowed");
foreach (var route in new[] { "/", "/observatory", "/library", "/settings" })
    Check(TerminalBoundary.OwnedUri("http://" + authority + route, "http://" + authority), "Owned page accepted");
Check(!TerminalBoundary.OwnedUri("http://" + authority + "/api/panes", "http://" + authority), "API path must not switch WebViews");
Check(!TerminalBoundary.OwnedUri("http://" + authority + "/library", "http://" + authority, terminal: true), "Terminal origin exposes only its terminal page");
foreach (var url in new[] { "http://" + authority + "@evil.example/", "http://localhost:45678/", "http://" + authority + "/?key=x", "http://" + authority + "/#x" })
    Check(!TerminalBoundary.OwnedUri(url, "http://" + authority), "Foreign/ambiguous URI rejected");
foreach (var json in new[] { "[]", "{\"paneId\":1,\"paneId\":2}", "{\"unknown\":1}", "{\"paneId\":NaN}" })
{
    try { using var parsed = TerminalBoundary.Parse(Encoding.UTF8.GetBytes(json), ["paneId"]); throw new Exception("Bad JSON accepted"); }
    catch (System.Text.Json.JsonException) { }
}
using (var parsed = TerminalBoundary.Parse(Encoding.UTF8.GetBytes("{\"paneId\":\"" + pane + "\"}"), ["paneId"]))
    Check(TerminalBoundary.Id(parsed.RootElement.GetProperty("paneId").GetString()!) == pane, "Canonical ID accepted");
try { TerminalBoundary.Id("ABC"); throw new Exception("Bad ID accepted"); } catch (ArgumentException) { }
Console.WriteLine("Terminal boundary: exact Host/Origin/auth, strict JSON, scoped one-use replay/expiry/race/shutdown checks passed.");

if (OperatingSystem.IsWindows())
{
    var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant();
    var launch = new TerminalLaunch("Command Prompt", executable, hash, ["/d"], Path.GetTempPath(),
        new() { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")! }, "new", null, null);
    var validate = typeof(VerifiedLaunch).GetMethod("Validate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    foreach (var unreviewed in new[] { launch with { Sha256 = new string('0', 64) },
        launch with { ProfileName = "Windows PowerShell", Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), Arguments = ["-NoLogo", "-NoProfile"], Sha256 = new string('0', 64) } })
    {
        try { validate.Invoke(null, [unreviewed]); throw new Exception("Unreviewed shell digest accepted by enrollment policy"); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is System.Security.SecurityException) { }
    }
    using (VerifiedLaunch.Open(launch)) { }
    var bash = new TerminalLaunch("Git Bash", @"C:\Program Files\Git\bin\bash.exe",
        "c6174b19689254556f318a8b507ed9c3bb317d4805917a281b226665b1a42a05",
        ["--noprofile", "--norc", "-i"], Path.GetTempPath(),
        new() { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")! }, "new", null, null);
    validate.Invoke(null, [bash]);
    using (VerifiedLaunch.Open(bash)) { }
    foreach (var badBash in new[] { bash with { Sha256 = new string('0', 64) },
        bash with { Executable = @"C:\Program Files\Git\usr\bin\bash.exe" },
        bash with { Arguments = ["-c", "echo not allowed"] },
        bash with { Mode = "resume", SourceSessionId = Guid.NewGuid().ToString(), SessionId = Guid.NewGuid().ToString() } })
    {
        try { validate.Invoke(null, [badBash]); throw new Exception("Unsafe Git Bash launch accepted"); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is ArgumentException or System.Security.SecurityException) { }
    }
    foreach (var bad in new[] { launch with { Sha256 = new string('0', 64) },
        launch with { ProfileName = null! },
        launch with { WorkingDirectory = @"\\localhost\c$\Windows" },
        launch with { Arguments = ["/d", "/c", "echo not allowed"] },
        launch with { Environment = new() { ["NODE_OPTIONS"] = "--require=untrusted.js" } },
        launch with { SourceSessionId = "not-a-session" },
        launch with { Executable = executable + ":stream" } })
    {
        try { using var check = VerifiedLaunch.Open(bad); throw new Exception("Unsafe launch accepted"); }
        catch (Exception error) when (error is ArgumentException or System.Security.SecurityException or IOException) { }
    }
    Console.WriteLine("Launch boundary: reviewed hash, local path, fixed profile arguments and environment checks passed (no child launched).");

    var profileFixture = Path.Combine(Path.GetTempPath(), "agent-foundry-profiles-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(profileFixture);
    try
    {
        File.WriteAllText(Path.Combine(profileFixture, "terminal-profiles.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1,
            profiles = new object[]
            {
                new { name = "Verified shell", executable, sha256 = hash },
                new { name = "Relative shell", executable = "cmd.exe", sha256 = hash },
                new { name = "WSL", executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"), sha256 = hash },
                new { name = "Commands", executable, sha256 = hash, arguments = new[] { "/c", "echo not allowed" } },
                new { name = 1, executable, sha256 = hash }
            }
        }));
        var profiles = TerminalProfiles.Load(profileFixture, out var warnings);
        Check(profiles.Any(x => x.Name == "Git Bash"), "fixed-location reviewed Git Bash was not detected");
        var configured = profiles.Single(x => x.Name == "Configured: Verified shell");
        Check(configured.Executable == executable && configured.Sha256 == hash && configured.Arguments.Length == 0,
            "deliberately configured zero-argument shell was not enrolled exactly");
        Check(profiles.All(x => x.Name is not ("Configured: Relative shell" or "Configured: WSL" or "Configured: Commands")) &&
              warnings.Count(x => x.StartsWith("Custom profile", StringComparison.Ordinal)) == 4,
            "unsafe custom shell records were not skipped individually with review warnings");
        var configuredLaunch = new TerminalLaunch(configured.Name, configured.Executable, configured.Sha256,
            configured.Arguments, profileFixture, new(), "new", null, null);
        using (VerifiedLaunch.Open(configuredLaunch)) { }
        try { using var check = VerifiedLaunch.Open(configuredLaunch with { Arguments = ["/c", "echo not allowed"] }); throw new Exception("Configured shell command arguments were accepted"); }
        catch (ArgumentException) { }
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string systemBash = Path.Combine(system, "bash.exe");
        var wslAliases = new[]
        {
            systemBash,
            Path.Combine(system, ".", "bash.exe"),
            Path.Combine(system, "drivers", "..", "bash.exe"),
            system.ToLowerInvariant().Replace('\\', '/') + "/bash.exe"
        };
        foreach (string wsl in new[] { Path.Combine(system, "wsl.exe") }.Concat(File.Exists(systemBash) ? wslAliases : []).Where(File.Exists))
        {
            string wslHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(wsl))).ToLowerInvariant();
            try { using var check = VerifiedLaunch.Open(configuredLaunch with { Executable = wsl, Sha256 = wslHash }); throw new Exception("Configured WSL profile was accepted"); }
            catch (System.Security.SecurityException) { }
        }
    }
    finally { Directory.Delete(profileFixture, recursive: true); }
}
}
catch (Exception error)
{
    Console.Error.WriteLine("FUNCTIONAL_CHECK_FAILED: " + error);
    Environment.ExitCode = 1;
}
