namespace AgentFoundry.Desktop;

// The offline checker links the real service and worker but makes native launch impossible.
public sealed class SafeJob : IDisposable
{
    public void Dispose() { }
}

public sealed class OwnedProcess : IDisposable, IAsyncDisposable
{
    public Stream StandardInput => throw new InvalidOperationException("native process access in offline check");
    public Stream StandardOutput => throw new InvalidOperationException("native process access in offline check");
    public int? ExitCode => null;
    public static OwnedProcess Start(string executable, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, SafeJob? lifetimeJob = null) =>
        throw new InvalidOperationException("native process launch in offline check");
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("native process wait in offline check");
    public void Stop(uint exitCode = 1) => throw new InvalidOperationException("native process stop in offline check");
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class PtySession : IDisposable, IAsyncDisposable
{
    public Stream Input => throw new InvalidOperationException("ConPTY access in offline check");
    public Stream Output => throw new InvalidOperationException("ConPTY access in offline check");
    public int? ExitCode => null;
    public static PtySession Start(string executable, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, short columns = 120, short rows = 30) =>
        throw new InvalidOperationException("ConPTY launch in offline check");
    public void Resize(short columns, short rows) => throw new InvalidOperationException("ConPTY resize in offline check");
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("ConPTY wait in offline check");
    public Task CloseAsync(Task outputDrain, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("ConPTY close in offline check");
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
