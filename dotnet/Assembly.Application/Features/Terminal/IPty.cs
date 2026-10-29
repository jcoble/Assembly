namespace Assembly.Application.Features.Terminal;

/// <summary>
/// One process on a pseudo-terminal. Disposing it kills the process's group, and the groups of what it started
/// (SIGKILL), and closes the master, so nothing it started outlives it.
/// </summary>
public interface IPty : IAsyncDisposable
{
    int Pid { get; }

    /// <summary>The process's exit code; a signal shows as 128 + its number, as .NET reports it.</summary>
    Task<int> Exited { get; }

    /// <summary>Reads what the program wrote, as it wrote it; 0 once nothing holds the terminal open any more.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    void Resize(int cols, int rows);
}
