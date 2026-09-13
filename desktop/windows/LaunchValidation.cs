using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentFoundry.Desktop;

public sealed record TerminalLaunch(string ProfileName, string Executable, string Sha256,
    string[] Arguments, string WorkingDirectory, Dictionary<string, string> Environment,
    string Mode, string? SourceSessionId, string? SessionId);

// Read/identity checks are repeated inside the contained worker after native confirmation.
// Handles deny replacement/reparse changes until CreateProcess has returned.
public sealed class VerifiedLaunch : IDisposable
{
    public const string CodexHash = "081e4de4be8e38fac6ed4d95e3b1a0b9f6d31c090ddc36e1696b349fe406f575";
    // Locally verified Microsoft Windows signatures, 2026-09-12. Updates require re-review.
    public static string ReviewedHash(string profile) => profile switch
    {
        "Codex" => CodexHash,
        "Command Prompt" => "97ac98b1a92c286054cce55239cfccdfc23a5517bd07fe693072c9ca96c7dabb",
        "Windows PowerShell" => "8bb6fa8c283b4d92120b1ef249a9b311b0f804d4cabbe9981159976c8be76a5e",
        "Git Bash" => "c6174b19689254556f318a8b507ed9c3bb317d4805917a281b226665b1a42a05",
        _ => throw new ArgumentException("This profile is not enrolled")
    };
    readonly List<SafeFileHandle> handles = [];
    readonly Dictionary<string, SafeFileHandle> opened = new(StringComparer.OrdinalIgnoreCase);

    public static VerifiedLaunch Open(TerminalLaunch launch)
    {
        Validate(launch);
        return OpenFiles(launch.Executable, launch.WorkingDirectory, launch.Sha256);
    }

    public static VerifiedLaunch OpenFiles(string executable, string cwd, string sha256)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required");
        if (sha256 is null || sha256.Length != 64 || sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("A reviewed SHA256 is required");
        var lease = new VerifiedLaunch();
        try
        {
            lease.OpenPath(cwd, directory: true);
            lease.CheckHash(executable, sha256);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public static VerifiedLaunch OpenBundle(string root, IReadOnlyDictionary<string, string> files)
    {
        var lease = new VerifiedLaunch();
        try
        {
            if (files.Count is < 10 or > 512 || !files.ContainsKey("runtime/python/python.exe") || !files.ContainsKey("app/monitor.py"))
                throw new SecurityException("Incomplete private runtime bundle");
            foreach (var pair in files)
            {
                if (pair.Key.Contains('\\') || pair.Key.Contains(':') || pair.Key.Split('/').Any(x => x is "" or "." or "..") ||
                    !new[] { "app", "runtime", "terminal" }.Contains(pair.Key.Split('/')[0]))
                    throw new SecurityException("Invalid bundle path");
                lease.CheckHash(Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar)), pair.Value);
            }
            // Isolated Python must not discover an extra unreviewed module in its search paths.
            var pending = new Stack<string>(new[] { "app", "runtime", "terminal" }.Select(x => Path.Combine(root, x)));
            int visited = 0;
            while (pending.TryPop(out var folder))
            {
                if (++visited > 512) throw new SecurityException("Unexpected bundle directory count");
                lease.OpenPath(folder, directory: true);
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SecurityException("Reparse points are not allowed in the private bundle");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!files.Keys.Any(x => x.StartsWith(relative + "/", StringComparison.Ordinal))) throw new SecurityException("Unexpected bundle directory");
                        pending.Push(entry);
                    }
                    else if (!files.ContainsKey(relative)) throw new SecurityException("Unexpected file in the private runtime bundle");
                }
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    void CheckHash(string path, string sha256)
    {
        if (sha256 is null || sha256.Length != 64 || sha256.Any(c => !char.IsAsciiHexDigitLower(c))) throw new SecurityException("Invalid bundle hash");
        var file = OpenPath(path, directory: false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long offset = 0;
        int count;
        while ((count = RandomAccess.Read(file, buffer, offset)) > 0)
        {
            offset += count;
            if (offset > 512L * 1024 * 1024) throw new SecurityException("Reviewed file size limit exceeded");
            hash.AppendData(buffer, 0, count);
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(sha256)))
            throw new SecurityException("A reviewed file changed; no process was launched");
    }

    static void Validate(TerminalLaunch item)
    {
        if (item is null || string.IsNullOrEmpty(item.ProfileName) || string.IsNullOrEmpty(item.Executable) || item.Arguments is null || item.Environment is null ||
            item.Arguments.Length > 16 || item.Arguments.Any(x => x is null || x.Length > 4096 || x.Contains('\0')))
            throw new ArgumentException("Invalid launch settings");
        if (item.SourceSessionId is not null) TerminalBoundary.Id(item.SourceSessionId);
        if (item.SessionId is not null) TerminalBoundary.Id(item.SessionId);
        if (!TerminalProfiles.IsConfigured(item.ProfileName) && item.Sha256 != ReviewedHash(item.ProfileName))
            throw new SecurityException("This profile build has not been verified");
        var system = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        string canonicalExecutable = Path.GetFullPath(item.Executable);
        switch (item.ProfileName)
        {
            case "Command Prompt":
                RequireExecutable(item.Executable, Path.Combine(system, "cmd.exe"));
                if (!item.Arguments.SequenceEqual(["/d"])) throw new ArgumentException("Unsupported shell arguments");
                RequireNew(item);
                break;
            case "Windows PowerShell":
                RequireExecutable(item.Executable, Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
                if (!item.Arguments.SequenceEqual(["-NoLogo", "-NoProfile"])) throw new ArgumentException("Unsupported shell arguments");
                RequireNew(item);
                break;
            case "Git Bash":
                RequireExecutable(item.Executable, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"));
                if (!item.Arguments.SequenceEqual(["--noprofile", "--norc", "-i"])) throw new ArgumentException("Unsupported shell arguments");
                RequireNew(item);
                break;
            case "Codex":
                RequireExecutable(item.Executable, Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "OpenAI", "Codex", "bin", "bffc5354119c8421", "codex.exe"));
                var expected = new List<string>();
                if (item.Mode is "resume" or "fork")
                {
                    if (item.SourceSessionId is null) throw new ArgumentException("A verified source session is required");
                    expected.Add(item.Mode); expected.Add(item.SourceSessionId);
                    if (item.Mode == "resume" ? item.SessionId != item.SourceSessionId : item.SessionId is not null)
                        throw new ArgumentException("Invalid destination session identity");
                }
                else RequireNew(item);
                expected.Add("-C"); expected.Add(item.WorkingDirectory);
                expected.AddRange(["--sandbox", "workspace-write", "--ask-for-approval", "on-request"]);
                // Permission flags are explicit; other reviewed CLI configuration remains user-owned.
                if (item.Arguments.Length == expected.Count + 2 && item.Arguments[^2] == "--model" &&
                    item.Arguments[^1].Length is > 0 and <= 100 && item.Arguments[^1].All(c => char.IsAsciiLetterOrDigit(c) || "-._".Contains(c)))
                { expected.Add("--model"); expected.Add(item.Arguments[^1]); }
                if (!item.Arguments.SequenceEqual(expected)) throw new ArgumentException("Unsupported Codex arguments");
                break;
            default:
                if (!TerminalProfiles.IsConfigured(item.ProfileName)) throw new ArgumentException("This profile is not enrolled");
                if (item.Arguments.Length != 0) throw new ArgumentException("Configured shells do not accept arguments");
                if (Path.GetFileName(canonicalExecutable).Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(canonicalExecutable).Equals("wslhost.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(canonicalExecutable, Path.Combine(system, "bash.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("WSL profiles are not supported");
                RequireNew(item);
                break;
        }
        string[] allowed = ["SystemRoot", "WINDIR", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "APPDATA",
            "TEMP", "TMP", "PATH", "PATHEXT", "TERM", "COLORTERM", "CODEX_HOME"];
        if (item.Environment.Count > allowed.Length || item.Environment.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Environment.Count ||
            item.Environment.Any(pair => !allowed.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || pair.Value is null ||
                pair.Value.Length > 8192 || pair.Value.Contains('\0')))
            throw new ArgumentException("Unsupported child environment");
    }

    static void RequireExecutable(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new SecurityException("Profile executable mismatch");
    }
    static void RequireNew(TerminalLaunch item)
    {
        if (item.Mode != "new" || item.SourceSessionId is not null || item.SessionId is not null)
            throw new ArgumentException("Invalid new-session identity");
    }

    SafeFileHandle OpenPath(string value, bool directory)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\0') ||
            !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\") || value[1] != ':' || value[2] != '\\' || value[2..].Contains(':'))
            throw new ArgumentException("Choose a local absolute path, without alternate streams");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var root = Path.GetPathRoot(full)!;
        if (new DriveInfo(root).DriveType != DriveType.Fixed) throw new SecurityException("Only local fixed drives are supported");
        var parts = full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        OpenOne(current, true);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            OpenOne(current, i < parts.Length - 1 || directory);
        }
        return opened[current];
    }

    void OpenOne(string path, bool directory)
    {
        if (opened.ContainsKey(path)) return;
        var handle = CreateFile(path, directory ? 0x80u : 0x80000000u, 1, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot lock the reviewed launch path", new Win32Exception()); }
        handles.Add(handle);
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot verify the launch path", new Win32Exception());
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
            throw new SecurityException("Reparse points or unexpected path types are not allowed");
        var resolved = new StringBuilder(1024);
        var size = GetFinalPathNameByHandle(handle, resolved, resolved.Capacity, 0);
        if (size == 0 || size >= resolved.Capacity || !string.Equals(
                Path.TrimEndingDirectorySeparator(resolved.ToString().Replace(@"\\?\", "")),
                Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("The launch path resolved to a different location");
        opened.Add(path, handle);
    }

    public void Dispose() { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); opened.Clear(); }

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);
}
