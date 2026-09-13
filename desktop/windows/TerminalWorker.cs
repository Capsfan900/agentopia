using System.Buffers.Binary;
using System.Text.Json;

namespace AgentFoundry.Desktop;

public static class TerminalLimits
{
    public const int MaxPanes = 8;
    public const int ReplayBytes = 1024 * 1024;
    public const int OutputChunkBytes = 64 * 1024;
    public const int AttachmentBacklogBytes = 512 * 1024;
    public const int InputBytes = 8 * 1024;
    public const int PrivateFrameBytes = 64 * 1024;
    public const int PrivateQueueFrames = 64;
}

internal enum WorkerControlKind : byte { Input = 1, Resize = 2, Close = 3 }

internal readonly record struct WorkerCommand(WorkerControlKind Kind, byte[] Data, short Columns, short Rows)
{
    public static WorkerCommand Input(byte[] data) => new(WorkerControlKind.Input, data, 0, 0);
    public static WorkerCommand Resize(short columns, short rows) => new(WorkerControlKind.Resize, [], columns, rows);
}

internal static class WorkerFrames
{
    static readonly byte[] Magic = "AFW1"u8.ToArray();
    static readonly string[] LaunchFields = ["ProfileName", "Executable", "Sha256", "Arguments",
        "WorkingDirectory", "Environment", "Mode", "SourceSessionId", "SessionId"];

    public static byte[] EncodeLaunch(TerminalLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(launch);
        if (payload.Length is 0 or > TerminalLimits.PrivateFrameBytes)
            throw new ArgumentException("The private launch request exceeds 64 KiB.", nameof(launch));
        return payload;
    }

    public static async Task WriteLaunchAsync(Stream stream, TerminalLaunch launch, CancellationToken cancellationToken)
    {
        byte[] payload = EncodeLaunch(launch);
        await WriteEncodedLaunchAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteEncodedLaunchAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > TerminalLimits.PrivateFrameBytes)
            throw new ArgumentException("The private launch request exceeds 64 KiB.", nameof(payload));
        await stream.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
        await WriteLengthAsync(stream, payload.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<TerminalLaunch> ReadLaunchAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] magic = new byte[Magic.Length];
        await ReadExactlyAsync(stream, magic, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Invalid private launch preamble.");
        int length = await ReadLengthAsync(stream, TerminalLimits.PrivateFrameBytes, cancellationToken).ConfigureAwait(false);
        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = TerminalBoundary.Parse(payload, LaunchFields);
        TerminalLaunch? launch = document.RootElement.Deserialize<TerminalLaunch>();
        return launch ?? throw new InvalidDataException("Private launch request was empty.");
    }

    public static Task WriteInputAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length > TerminalLimits.InputBytes) throw new ArgumentException("Terminal input exceeds 8 KiB.", nameof(data));
        return WriteFrameAsync(stream, WorkerControlKind.Input, data, cancellationToken);
    }

    public static Task WriteResizeAsync(Stream stream, short columns, short rows, CancellationToken cancellationToken)
    {
        ValidateDimensions(columns, rows);
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(payload, columns);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), rows);
        return WriteFrameAsync(stream, WorkerControlKind.Resize, payload, cancellationToken);
    }

    public static Task WriteCloseAsync(Stream stream, CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, WorkerControlKind.Close, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public static async Task<WorkerCommand> ReadControlAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[5];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var kind = (WorkerControlKind)header[0];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        int maximum = kind switch
        {
            WorkerControlKind.Input => TerminalLimits.InputBytes,
            WorkerControlKind.Resize => 4,
            WorkerControlKind.Close => 0,
            _ => throw new InvalidDataException("Unknown private worker frame.")
        };
        if (length < 0 || length > maximum || kind is WorkerControlKind.Resize && length != 4 ||
            kind is WorkerControlKind.Close && length != 0)
            throw new InvalidDataException("Invalid private worker frame length.");
        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (kind == WorkerControlKind.Resize)
        {
            short columns = BinaryPrimitives.ReadInt16LittleEndian(payload);
            short rows = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(2));
            ValidateDimensions(columns, rows);
            return WorkerCommand.Resize(columns, rows);
        }
        return kind == WorkerControlKind.Input ? WorkerCommand.Input(payload) :
            new WorkerCommand(WorkerControlKind.Close, [], 0, 0);
    }

    static async Task WriteFrameAsync(Stream stream, WorkerControlKind kind, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[5];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty) await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task WriteLengthAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    static async Task<int> ReadLengthAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[4];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (length is <= 0 || length > maximum) throw new InvalidDataException("Invalid private frame length.");
        return length;
    }

    static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int count = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
    }

    static void ValidateDimensions(short columns, short rows)
    {
        if (columns is < 2 or > 500 || rows is < 2 or > 300)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal size is outside the supported range.");
    }
}

internal enum WorkerStopReason { ControlClosed, ChildExited }

internal static class WorkerLifetime
{
    public static async Task<WorkerStopReason> WaitAsync(Task controls, Task childExit)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(childExit);
        Task first = await Task.WhenAny(controls, childExit).ConfigureAwait(false);
        if (first == controls)
        {
            await controls.ConfigureAwait(false);
            return WorkerStopReason.ControlClosed;
        }
        await childExit.ConfigureAwait(false);
        return WorkerStopReason.ChildExited;
    }
}

public static class TerminalWorker
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Stream privateInput = Console.OpenStandardInput();
            Stream rawOutput = Console.OpenStandardOutput();
            TerminalLaunch launch = await WorkerFrames.ReadLaunchAsync(privateInput, cancellationToken).ConfigureAwait(false);
            PtySession session;
            using (VerifiedLaunch.Open(launch))
                session = PtySession.Start(launch.Executable, launch.Arguments, launch.WorkingDirectory, launch.Environment);
            Task outputDrain = DrainOutputAsync(session.Output, rawOutput);
            Task<int> childExit = session.WaitForExitAsync();
            using var controlsCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task controls = ProcessControlsAsync(session, privateInput, controlsCancellation.Token);
            try
            {
                await WorkerLifetime.WaitAsync(controls, childExit).ConfigureAwait(false);
            }
            finally
            {
                controlsCancellation.Cancel();
                await session.CloseAsync(outputDrain, CancellationToken.None).ConfigureAwait(false);
            }
            int exitCode = await childExit.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or JsonException or EndOfStreamException)
        { return 64; }
        catch { return 70; }
    }

    static async Task ProcessControlsAsync(PtySession session, Stream privateInput, CancellationToken cancellationToken)
    {
        var inputRate = new TerminalRateGate(128, TimeSpan.FromSeconds(1));
        var resizeRate = new TerminalRateGate(20, TimeSpan.FromSeconds(1));
        try
        {
            while (true)
            {
                WorkerCommand command = await WorkerFrames.ReadControlAsync(privateInput, cancellationToken).ConfigureAwait(false);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (command.Kind == WorkerControlKind.Close) return;
                if (command.Kind == WorkerControlKind.Input)
                {
                    if (!inputRate.TryTake(now)) throw new InvalidDataException("Worker input rate exceeded.");
                    await session.Input.WriteAsync(command.Data, cancellationToken).ConfigureAwait(false);
                    await session.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (!resizeRate.TryTake(now)) throw new InvalidDataException("Worker resize rate exceeded.");
                    session.Resize(command.Columns, command.Rows);
                }
            }
        }
        catch (EndOfStreamException) { }
    }

    static async Task DrainOutputAsync(Stream source, Stream destination)
    {
        byte[] buffer = new byte[TerminalLimits.OutputChunkBytes];
        bool destinationOpen = true;
        while (true)
        {
            int count = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) break;
            if (!destinationOpen) continue;
            try
            {
                await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                await destination.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // Keep draining ConPTY after the private stdout consumer has gone away.
                destinationOpen = false;
            }
        }
    }
}
