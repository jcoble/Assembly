namespace Assembly.Application.Features.Terminal;

/// <summary>
/// One process on a pseudo-terminal. Disposing it kills the process's group (SIGKILL) and closes the master, so
/// nothing it started outlives it.
/// </summary>
public interface IPty : IAsyncDisposable
{
    int Pid { get; }

    /// <summary>The process's end. It completes once, whatever ended it.</summary>
    Task<PtyExit> Exited { get; }

    /// <summary>Reads what the program wrote, as it wrote it; 0 once the terminal has closed.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    void Resize(int cols, int rows);
}
