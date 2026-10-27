using Assembly.Application.Common.Topics;

namespace Assembly.Application.Features.Terminal;

/// <summary>
/// This machine's terminals, by id. Terminals outlive the sockets that use them (terminal#5); disposing the host, when
/// the backend stops, closes every one (terminal#8).
/// </summary>
internal sealed class TerminalHost : IAsyncDisposable
{
    public const int DefaultCols = 96;
    public const int DefaultRows = 28;

    public TerminalHost(IPtyStarter pty, ITopics topics, TimeProvider time) => throw new NotImplementedException();

    /// <summary>
    /// Starts a terminal in <see cref="StartArgs.Cwd"/>, or answers the owner's terminal while it still runs
    /// (terminal#9). Fails with the folder's path when it doesn't exist (terminal#1).
    /// </summary>
    public TerminalInfo Start(StartArgs args) => throw new NotImplementedException();

    public IReadOnlyList<TerminalInfo> List() => throw new NotImplementedException();

    /// <summary>Fails for a terminal that's closed or whose process has ended.</summary>
    public ValueTask WriteAsync(string terminal, byte[] data, CancellationToken cancellationToken) => throw new NotImplementedException();

    public void Resize(string terminal, int cols, int rows) => throw new NotImplementedException();

    public OutputPiece Read(string terminal, long offset) => throw new NotImplementedException();

    public (long Seq, IReadOnlyList<string> Rows) State(string terminal, long? after) => throw new NotImplementedException();

    /// <summary>Ends the terminal and lets go of its output; ok for one already closed.</summary>
    public ValueTask CloseAsync(string terminal) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
