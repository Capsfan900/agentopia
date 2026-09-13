using System.Security;
using System.Text.Json;

namespace AgentFoundry.Desktop;

public sealed record TerminalProfile(string Name, string Executable, string Sha256, string[] Arguments);

public static class TerminalProfiles
{
    const string ConfiguredPrefix = "Configured: ";

    public static IReadOnlyList<TerminalProfile> Load(string dataDirectory, out string[] warnings)
    {
        var result = new List<TerminalProfile>();
        var skipped = new List<string>();
        foreach (var name in new[] { "Codex", "Windows PowerShell", "Command Prompt", "Git Bash" })
        {
            var profile = Known(name);
            if (!File.Exists(profile.Executable)) continue;
            if (name != "Git Bash") { result.Add(profile); continue; }
            try { using (VerifiedLaunch.OpenFiles(profile.Executable, dataDirectory, profile.Sha256)) result.Add(profile); }
            catch (Exception error) when (Expected(error)) { skipped.Add("Git Bash was detected but skipped: " + error.Message); }
        }

        string path = Path.Combine(dataDirectory, "terminal-profiles.json");
        if (File.Exists(path))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16384)
                    throw new InvalidDataException("the file is a reparse point or exceeds 16 KiB");
                using var document = TerminalBoundary.Parse(File.ReadAllBytes(path), ["schema_version", "profiles"]);
                var root = document.RootElement;
                if (root.GetProperty("schema_version").ValueKind != JsonValueKind.Number ||
                    !root.GetProperty("schema_version").TryGetInt32(out int version) || version != 1 ||
                    root.GetProperty("profiles").ValueKind != JsonValueKind.Array)
                    throw new JsonException("schema_version 1 and a profiles array are required");
                var rows = root.GetProperty("profiles").EnumerateArray().ToArray();
                if (rows.Length > 8) throw new JsonException("at most eight custom profiles are allowed");
                for (int index = 0; index < rows.Length; index++)
                {
                    try
                    {
                        var row = rows[index];
                        if (row.ValueKind != JsonValueKind.Object) throw new JsonException("an object is required");
                        var fields = row.EnumerateObject().Select(x => x.Name).ToArray();
                        string[] expected = ["name", "executable", "sha256"];
                        if (fields.Length != expected.Length || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length ||
                            fields.Except(expected, StringComparer.Ordinal).Any())
                            throw new JsonException("only name, executable and sha256 are allowed");
                        var nameValue = row.GetProperty("name");
                        var executableValue = row.GetProperty("executable");
                        var hashValue = row.GetProperty("sha256");
                        if (nameValue.ValueKind != JsonValueKind.String || executableValue.ValueKind != JsonValueKind.String ||
                            hashValue.ValueKind != JsonValueKind.String) throw new JsonException("profile values must be strings");
                        string name = nameValue.GetString()!;
                        string executable = executableValue.GetString()!;
                        string sha256 = hashValue.GetString()!;
                        if (name.Length is < 1 or > 64 || name != name.Trim() || name.Any(char.IsControl))
                            throw new ArgumentException("name must be 1-64 printable characters without outer whitespace");
                        var profile = new TerminalProfile(ConfiguredPrefix + name, executable, sha256, []);
                        if (result.Any(x => string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                            throw new ArgumentException("name is duplicated");
                        using (VerifiedLaunch.Open(new(profile.Name, profile.Executable, profile.Sha256, [], dataDirectory, new(), "new", null, null))) { }
                        result.Add(profile);
                    }
                    catch (Exception error) when (Expected(error)) { skipped.Add($"Custom profile {index + 1} was skipped: {error.Message}"); }
                }
            }
            catch (Exception error) when (Expected(error)) { skipped.Add("Custom profile file was skipped: " + error.Message); }
        }
        warnings = skipped.ToArray();
        return result;
    }

    public static bool IsConfigured(string name) => name?.StartsWith(ConfiguredPrefix, StringComparison.Ordinal) == true &&
        name.Length > ConfiguredPrefix.Length && name.Length <= ConfiguredPrefix.Length + 64;

    static TerminalProfile Known(string name)
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string executable = name switch
        {
            "Command Prompt" => Path.Combine(system, "cmd.exe"),
            "Windows PowerShell" => Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "Git Bash" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            "Codex" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin", "bffc5354119c8421", "codex.exe"),
            _ => throw new ArgumentException("This profile is not enrolled")
        };
        string[] arguments = name switch
        {
            "Command Prompt" => ["/d"],
            "Windows PowerShell" => ["-NoLogo", "-NoProfile"],
            "Git Bash" => ["--noprofile", "--norc", "-i"],
            "Codex" => [],
            _ => throw new ArgumentException("This profile is not enrolled")
        };
        return new(name, executable, VerifiedLaunch.ReviewedHash(name), arguments);
    }

    static bool Expected(Exception error) => error is ArgumentException or IOException or JsonException or SecurityException or UnauthorizedAccessException;
}
