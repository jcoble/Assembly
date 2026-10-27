using Assembly.Application.Common.Topics;

namespace Assembly.Application.Features.Terminal;

/// <summary>
/// One open terminal: its PTY, its output ring and its exit. A reader loop appends each read to the ring and publishes
/// it as one row on <c>terminal</c> under one lock, so a new subscriber's state and the rows after it neither overlap
/// nor leave a gap. Once the reader hits end of file and the process has ended, it publishes the exit on
/// <c>terminals</c>, so the last output always goes first (terminal#7).
/// </summary>
internal sealed class RunningTerminal : IAsyncDisposable
{
    public RunningTerminal(TerminalInfo info, IPty pty, ITopics topics) => throw new NotImplementedException();

    public TerminalInfo Info => throw new NotImplementedException();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new NotImplementedException();

    public void Resize(int cols, int rows) => throw new NotImplementedException();

    public OutputPiece Read(long offset) => throw new NotImplementedException();

    /// <summary>The <c>terminal</c> topic's state for a sub resuming after <paramref name="after"/> (see TerminalFeature).</summary>
    public (long Seq, IReadOnlyList<string> Rows) State(long? after) => throw new NotImplementedException();

    /// <summary>Ends the process group, stops the reader and lets go of the output.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
