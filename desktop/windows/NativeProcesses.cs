using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentFoundry.Desktop;

public sealed class SafeJob : IDisposable
{
    SafeFileHandle? _handle;

    public SafeJob()
    {
        Native.EnsureWindowsAndNonElevated();
        nint handle = Native.CreateJobObjectW(0, null);
        if (handle == 0)
            throw new Win32Exception();
        _handle = new SafeFileHandle(handle, ownsHandle: true);
        Native.ClearInheritance(handle);
        var limits = new Native.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new Native.JobObjectBasicLimitInformation
            {
                LimitFlags = Native.JobObjectLimitKillOnJobClose,
            },
        };
        if (!Native.SetInformationJobObject(handle, Native.JobObjectExtendedLimitInformationClass,
                                             ref limits, (uint)Marshal.SizeOf<Native.JobObjectExtendedLimitInformation>()))
        {
            _handle.Dispose();
            _handle = null;
            throw new Win32Exception();
        }
    }

    internal nint Handle => _handle is { IsInvalid: false, IsClosed: false } handle
        ? handle.DangerousGetHandle()
        : throw new ObjectDisposedException(nameof(SafeJob));

    public IReadOnlyList<int> AssociatedProcessIds => Native.QueryJobProcessIds(Handle);

    public bool Owns(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Owns(process.SafeHandle.DangerousGetHandle());
    }

    public bool Owns(nint nativeProcessHandle)
    {
        if (nativeProcessHandle == 0)
            throw new ArgumentException("A process handle is required.", nameof(nativeProcessHandle));
        if (!Native.IsProcessInJob(nativeProcessHandle, Handle, out bool result))
            throw new Win32Exception();
        return result;
    }

    public void Terminate(uint exitCode = 1)
    {
        if (!Native.TerminateJobObject(Handle, exitCode))
            throw new Win32Exception();
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}

public sealed class OwnedProcess : IDisposable, IAsyncDisposable
{
    readonly SafeJob _job;
    readonly SafeFileHandle _process;
    readonly Task<int> _exit;
    bool _disposed;

    OwnedProcess(SafeJob job, SafeFileHandle process, int processId,
                 FileStream standardInput, FileStream standardOutput)
    {
        _job = job;
        _process = process;
        ProcessId = processId;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        _exit = Native.WaitForExitAsync(process);
    }

    public int ProcessId { get; }
    public Stream StandardInput { get; }
    public Stream StandardOutput { get; }
    public bool HasExited => Native.HasExited(_process);
    public int? ExitCode => HasExited ? Native.GetExitCode(_process) : null;

    public static OwnedProcess Start(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        SafeJob? lifetimeJob = null)
    {
        Native.EnsureWindowsAndNonElevated();
        Native.ValidateLaunch(executable, args, workingDirectory, environment);

        var paneJob = new SafeJob();
        SafeFileHandle? stdinRead = null;
        SafeFileHandle? stdinWrite = null;
        SafeFileHandle? stdoutRead = null;
        SafeFileHandle? stdoutWrite = null;
        SafeFileHandle? stderr = null;
        SafeFileHandle? process = null;
        try
        {
            (stdinRead, stdinWrite) = Native.CreatePipe(inheritable: true);
            (stdoutRead, stdoutWrite) = Native.CreatePipe(inheritable: true);
            Native.ClearInheritance(stdinWrite.DangerousGetHandle());
            Native.ClearInheritance(stdoutRead.DangerousGetHandle());
            stderr = Native.OpenNull(inheritable: true);

            using var attributes = new Native.AttributeList(2);
            nint[] jobs = lifetimeJob is null
                ? [paneJob.Handle]
                : [lifetimeJob.Handle, paneJob.Handle];
            attributes.AddHandles(Native.ProcThreadAttributeJobList, jobs);
            attributes.AddHandles(Native.ProcThreadAttributeHandleList,
                                  [stdinRead.DangerousGetHandle(), stdoutWrite.DangerousGetHandle(),
                                   stderr.DangerousGetHandle()]);

            var startup = Native.StartupInfoEx.Create(attributes.Pointer);
            startup.StartupInfo.Flags = Native.StartfUseStdHandles;
            startup.StartupInfo.StandardInput = stdinRead.DangerousGetHandle();
            startup.StartupInfo.StandardOutput = stdoutWrite.DangerousGetHandle();
            startup.StartupInfo.StandardError = stderr.DangerousGetHandle();
            Native.ProcessInformation info = Native.CreateProcess(
                executable, args, workingDirectory, environment, inheritHandles: true,
                Native.ExtendedStartupInfoPresent | Native.CreateUnicodeEnvironment | Native.CreateNoWindow,
                ref startup);
            Native.CloseHandle(info.Thread);
            process = new SafeFileHandle(info.Process, ownsHandle: true);

            stdinRead.Dispose();
            stdinRead = null;
            stdoutWrite.Dispose();
            stdoutWrite = null;
            stderr.Dispose();
            stderr = null;

            if (!paneJob.Owns(process.DangerousGetHandle())
                || (lifetimeJob is not null && !lifetimeJob.Owns(process.DangerousGetHandle())))
            {
                paneJob.Terminate();
                throw new InvalidOperationException("The child did not enter every required Job at creation.");
            }

            var input = new FileStream(stdinWrite, FileAccess.Write, 4096, isAsync: false);
            stdinWrite = null;
            var output = new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false);
            stdoutRead = null;
            var result = new OwnedProcess(paneJob, process, info.ProcessId, input, output);
            paneJob = null!;
            process = null;
            return result;
        }
        catch
        {
            try { paneJob?.Terminate(); } catch { }
            paneJob?.Dispose();
            process?.Dispose();
            stdinRead?.Dispose();
            stdinWrite?.Dispose();
            stdoutRead?.Dispose();
            stdoutWrite?.Dispose();
            stderr?.Dispose();
            throw;
        }
    }

    internal bool OwnsForCheck(Process process) => _job.Owns(process);

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.WaitAsync(cancellationToken);

    public void Stop(uint exitCode = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _job.Terminate(exitCode);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _job.Dispose();
        StandardInput.Dispose();
        StandardOutput.Dispose();
        _process.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class PtySession : IDisposable, IAsyncDisposable
{
    readonly SafeJob _job;
    readonly SafeFileHandle _process;
    readonly Task<int> _exit;
    readonly object _closeLock = new();
    nint _pseudoConsole;
    Task? _close;

    PtySession(SafeJob job, nint pseudoConsole, SafeFileHandle process, int processId,
               FileStream input, FileStream output)
    {
        _job = job;
        _pseudoConsole = pseudoConsole;
        _process = process;
        ProcessId = processId;
        Input = input;
        Output = output;
        _exit = Native.WaitForExitAsync(process);
    }

    public int ProcessId { get; }
    public Stream Input { get; }
    public Stream Output { get; }
    public bool HasExited => Native.HasExited(_process);
    public int? ExitCode => HasExited ? Native.GetExitCode(_process) : null;

    public static PtySession Start(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns = 120,
        short rows = 30)
    {
        Native.ValidateDimensions(columns, rows);
        Native.EnsureWindowsAndNonElevated();
        Native.ValidateLaunch(executable, args, workingDirectory, environment);
        if (!Native.IsProcessInJob(Native.GetCurrentProcess(), 0, out bool contained) || !contained)
            throw new InvalidOperationException("ConPTY may start only inside a Job-contained worker.");

        var paneJob = new SafeJob();
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafeFileHandle? process = null;
        nint pseudoConsole = 0;
        try
        {
            (inputRead, inputWrite) = Native.CreatePipe(inheritable: false);
            (outputRead, outputWrite) = Native.CreatePipe(inheritable: false);
            Native.ThrowIfFailed(Native.CreatePseudoConsole(
                new Native.Coord(columns, rows), inputRead.DangerousGetHandle(),
                outputWrite.DangerousGetHandle(), 0, out pseudoConsole));

            using var attributes = new Native.AttributeList(2);
            attributes.AddHandles(Native.ProcThreadAttributeJobList, [paneJob.Handle]);
            attributes.AddPseudoConsole(pseudoConsole);
            var startup = Native.StartupInfoEx.Create(attributes.Pointer);
            startup.StartupInfo.Flags = Native.StartfUseStdHandles;
            Native.ProcessInformation info = Native.CreateProcess(
                executable, args, workingDirectory, environment, inheritHandles: false,
                Native.ExtendedStartupInfoPresent | Native.CreateUnicodeEnvironment,
                ref startup);
            process = new SafeFileHandle(info.Process, ownsHandle: true);
            Native.CloseHandle(info.Thread);
            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;
            if (!paneJob.Owns(process.DangerousGetHandle()))
            {
                paneJob.Terminate();
                throw new InvalidOperationException("The ConPTY child escaped pane Job containment.");
            }

            var input = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false);
            inputWrite = null;
            var output = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false);
            outputRead = null;
            var result = new PtySession(paneJob, pseudoConsole, process, info.ProcessId, input, output);
            paneJob = null!;
            pseudoConsole = 0;
            process = null;
            return result;
        }
        catch
        {
            try { paneJob?.Terminate(); } catch { }
            paneJob?.Dispose();
            paneJob = null!;
            if (process is not null)
                Native.WaitForSingleObject(process.DangerousGetHandle(), Native.Infinite);
            if (pseudoConsole != 0)
                Native.ClosePseudoConsole(pseudoConsole);
            process?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            throw;
        }
    }

    public void Resize(short columns, short rows)
    {
        Native.ValidateDimensions(columns, rows);
        nint pseudoConsole = Volatile.Read(ref _pseudoConsole);
        if (pseudoConsole == 0)
            throw new ObjectDisposedException(nameof(PtySession));
        Native.ThrowIfFailed(Native.ResizePseudoConsole(pseudoConsole, new Native.Coord(columns, rows)));
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.WaitAsync(cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken = default)
        => CloseAsyncCore(null, cancellationToken);

    public Task CloseAsync(Task outputDrain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputDrain);
        return CloseAsyncCore(outputDrain, cancellationToken);
    }

    Task CloseAsyncCore(Task? outputDrain, CancellationToken cancellationToken)
    {
        Task close;
        lock (_closeLock)
            close = _close ??= CloseCoreAsync(outputDrain);
        return close.WaitAsync(cancellationToken);
    }

    async Task CloseCoreAsync(Task? outputDrain = null)
    {
        Input.Dispose();
        Task drain = outputDrain ?? DrainOutputAsync();
        if (await Task.WhenAny(_exit, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != _exit)
            _job.Terminate();
        await _exit.ConfigureAwait(false);

        nint pseudoConsole = Interlocked.Exchange(ref _pseudoConsole, 0);
        if (pseudoConsole != 0)
            await Task.Run(() => Native.ClosePseudoConsole(pseudoConsole)).ConfigureAwait(false);
        try
        {
            await drain.ConfigureAwait(false);
        }
        finally
        {
            Output.Dispose();
            _job.Dispose();
            _process.Dispose();
        }
    }

    async Task DrainOutputAsync()
    {
        try
        {
            await Output.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose() => CloseAsync().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}

static class Native
{
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const int JobObjectBasicProcessIdListClass = 3;
    internal const int JobObjectExtendedLimitInformationClass = 9;
    internal const nuint ProcThreadAttributeHandleList = 0x00020002;
    internal const nuint ProcThreadAttributeJobList = 0x0002000D;
    internal const nuint ProcThreadAttributePseudoConsole = 0x00020016;
    internal const uint StartfUseStdHandles = 0x00000100;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    const uint HandleFlagInherit = 0x00000001;
    const uint TokenQuery = 0x0008;
    const int TokenElevation = 20;
    const uint GenericWrite = 0x40000000;
    const uint FileShareRead = 0x00000001;
    const uint FileShareWrite = 0x00000002;
    const uint OpenExisting = 3;
    internal const uint Infinite = 0xFFFFFFFF;
    const uint WaitObject0 = 0;
    const uint WaitTimeout = 258;
    const int ErrorMoreData = 234;

    internal static void EnsureWindowsAndNonElevated()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows native process containment is required.");
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out nint token))
            throw new Win32Exception();
        try
        {
            int size = Marshal.SizeOf<TokenElevationInfo>();
            nint value = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenElevation, value, size, out int returned)
                    || returned < size)
                    throw new Win32Exception();
                if (Marshal.PtrToStructure<TokenElevationInfo>(value).TokenIsElevated != 0)
                    throw new UnauthorizedAccessException("Elevated process hosting is not allowed.");
            }
            finally
            {
                Marshal.FreeHGlobal(value);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    internal static void ValidateDimensions(short columns, short rows)
    {
        if (columns is < 2 or > 500)
            throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 2 or > 300)
            throw new ArgumentOutOfRangeException(nameof(rows));
    }

    internal static void ValidateLaunch(
        string executable, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0')
            || !Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Executable must be an absolute path without NUL characters.", nameof(executable));
        if (string.IsNullOrWhiteSpace(workingDirectory) || workingDirectory.Contains('\0')
            || !Path.IsPathFullyQualified(workingDirectory))
            throw new ArgumentException("Working directory must be an absolute path without NUL characters.", nameof(workingDirectory));
        foreach (string arg in args)
            if (arg is null || arg.Contains('\0'))
                throw new ArgumentException("Arguments must not contain nulls or NUL characters.", nameof(args));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in environment)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=') || name.Contains('\0')
                || value is null || value.Contains('\0') || !names.Add(name))
                throw new ArgumentException("Environment entries are invalid or ambiguous.", nameof(environment));
        }
    }

    internal static (SafeFileHandle Read, SafeFileHandle Write) CreatePipe(bool inheritable)
    {
        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = inheritable,
        };
        if (!CreatePipe(out nint read, out nint write, ref security, 0))
            throw new Win32Exception();
        return (new SafeFileHandle(read, ownsHandle: true), new SafeFileHandle(write, ownsHandle: true));
    }

    internal static SafeFileHandle OpenNull(bool inheritable)
    {
        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = inheritable,
        };
        nint handle = CreateFileW("NUL", GenericWrite, FileShareRead | FileShareWrite,
                                  ref security, OpenExisting, 0, 0);
        if (handle == -1)
            throw new Win32Exception();
        return new SafeFileHandle(handle, ownsHandle: true);
    }

    internal static void ClearInheritance(nint handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, 0))
            throw new Win32Exception();
    }

    internal static ProcessInformation CreateProcess(
        string executable, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, bool inheritHandles,
        uint flags, ref StartupInfoEx startup)
    {
        var commandLine = new StringBuilder(BuildCommandLine(executable, args));
        nint environmentBlock = BuildEnvironmentBlock(environment);
        try
        {
            if (!CreateProcessW(executable, commandLine, 0, 0, inheritHandles, flags,
                                environmentBlock, workingDirectory, ref startup, out ProcessInformation info))
                throw new Win32Exception();
            return info;
        }
        finally
        {
            Marshal.FreeHGlobal(environmentBlock);
        }
    }

    static string BuildCommandLine(string executable, IReadOnlyList<string> args) =>
        string.Join(' ', new[] { executable }.Concat(args).Select(QuoteArgument));

    static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return value;
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append(character);
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    static nint BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        string text = string.Join('\0', environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        char[] characters = text.ToCharArray();
        nint block = Marshal.AllocHGlobal(characters.Length * sizeof(char));
        Marshal.Copy(characters, 0, block, characters.Length);
        return block;
    }

    internal static Task<int> WaitForExitAsync(SafeFileHandle process)
    {
        bool added = false;
        process.DangerousAddRef(ref added);
        return Task.Run(() =>
        {
            try
            {
                uint wait = WaitForSingleObject(process.DangerousGetHandle(), Infinite);
                if (wait != WaitObject0)
                    throw new Win32Exception();
                return GetExitCode(process);
            }
            finally
            {
                if (added)
                    process.DangerousRelease();
            }
        });
    }

    internal static bool HasExited(SafeFileHandle process)
    {
        uint wait = WaitForSingleObject(process.DangerousGetHandle(), 0);
        return wait switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            _ => throw new Win32Exception(),
        };
    }

    internal static int GetExitCode(SafeFileHandle process)
    {
        if (!GetExitCodeProcess(process.DangerousGetHandle(), out uint exitCode))
            throw new Win32Exception();
        return unchecked((int)exitCode);
    }

    internal static IReadOnlyList<int> QueryJobProcessIds(nint job)
    {
        const int headerSize = sizeof(uint) * 2;
        const int maximumProcessIds = 4_096;
        int capacity = 16;
        for (int attempt = 0; attempt < 9; attempt++)
        {
            int bufferSize = checked(headerSize + capacity * IntPtr.Size);
            nint buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                bool success = QueryInformationJobObject(
                    job, JobObjectBasicProcessIdListClass, buffer, (uint)bufferSize, out _);
                int error = Marshal.GetLastWin32Error();
                if (!success && error != ErrorMoreData)
                    throw new Win32Exception(error);
                uint assigned = unchecked((uint)Marshal.ReadInt32(buffer));
                uint listed = unchecked((uint)Marshal.ReadInt32(buffer, sizeof(uint)));
                if (assigned > maximumProcessIds || listed > maximumProcessIds)
                    throw new InvalidOperationException(
                        $"Job process inventory exceeds the {maximumProcessIds} entry safety bound.");

                if (success && assigned == listed && listed <= capacity)
                {
                    var processIds = new int[listed];
                    for (int index = 0; index < processIds.Length; index++)
                    {
                        long processId = Marshal.ReadIntPtr(
                            buffer, headerSize + index * IntPtr.Size).ToInt64();
                        if (processId is <= 0 or > int.MaxValue)
                            throw new InvalidOperationException("Job returned an invalid process identifier.");
                        processIds[index] = (int)processId;
                    }
                    return Array.AsReadOnly(processIds);
                }

                int required = checked((int)Math.Max(assigned, listed));
                capacity = Math.Min(maximumProcessIds, Math.Max(capacity * 2, required));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new InvalidOperationException("Job process inventory did not stabilize within nine queries.");
    }

    internal static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    internal sealed class AttributeList : IDisposable
    {
        readonly List<nint> _values = [];
        nint _pointer;

        internal AttributeList(int count)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(0, count, 0, ref size);
            _pointer = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(_pointer, count, 0, ref size))
            {
                Marshal.FreeHGlobal(_pointer);
                _pointer = 0;
                throw new Win32Exception();
            }
        }

        internal nint Pointer => _pointer != 0 ? _pointer : throw new ObjectDisposedException(nameof(AttributeList));

        internal void AddPseudoConsole(nint pseudoConsole)
        {
            if (!UpdateProcThreadAttribute(Pointer, 0, ProcThreadAttributePseudoConsole,
                                           pseudoConsole, (nuint)IntPtr.Size, 0, 0))
                throw new Win32Exception();
        }

        internal void AddHandles(nuint attribute, IReadOnlyList<nint> handles)
        {
            nint value = Marshal.AllocHGlobal(IntPtr.Size * handles.Count);
            for (int index = 0; index < handles.Count; index++)
                Marshal.WriteIntPtr(value, index * IntPtr.Size, handles[index]);
            _values.Add(value);
            if (!UpdateProcThreadAttribute(Pointer, 0, attribute, value,
                                           (nuint)(IntPtr.Size * handles.Count), 0, 0))
                throw new Win32Exception();
        }

        public void Dispose()
        {
            if (_pointer == 0)
                return;
            DeleteProcThreadAttributeList(_pointer);
            Marshal.FreeHGlobal(_pointer);
            _pointer = 0;
            foreach (nint value in _values)
                Marshal.FreeHGlobal(value);
            _values.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        internal int Length;
        internal nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        internal int Size;
        internal nint Reserved;
        internal nint Desktop;
        internal nint Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2Size;
        internal nint Reserved2;
        internal nint StandardInput;
        internal nint StandardOutput;
        internal nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal nint AttributeList;

        internal static StartupInfoEx Create(nint attributes) => new()
        {
            StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
            AttributeList = attributes,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal nint Process;
        internal nint Thread;
        internal int ProcessId;
        internal int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenElevationInfo { internal uint TokenIsElevated; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord
    {
        internal short X;
        internal short Y;
        internal Coord(short x, short y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint CreateJobObjectW(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(nint job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryInformationJobObject(nint job, int informationClass,
        nint information, uint informationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(out nint readPipe, out nint writePipe,
        ref SecurityAttributes pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        ref SecurityAttributes securityAttributes, uint creationDisposition, uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, nint environment, string currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount,
        uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateProcThreadAttribute(nint attributeList, uint flags, nuint attribute,
        nint value, nuint size, nint previousValue, nint returnSize);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(Coord size, nint input, nint output,
        uint flags, out nint pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ResizePseudoConsole(nint pseudoConsole, Coord size);

    [DllImport("kernel32.dll")]
    internal static extern void ClosePseudoConsole(nint pseudoConsole);

    [DllImport("kernel32.dll")]
    internal static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(nint token, int tokenInformationClass,
        nint tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(nint process, uint exitCode);

}
