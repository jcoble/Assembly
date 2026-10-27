using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;

namespace Assembly.Application.Features.Terminal;

/// <summary>
/// One open terminal: its PTY, its output ring and its exit. A reader loop appends each read to the ring under the
/// terminal's lock, then publishes it as one row on <c>terminal</c> once it has let go: a subscriber's state runs under
/// the Topics lock and then takes this one, so publishing under it would take them the other way round. A row the
/// state already holds is dropped by its seq (SocketDispatcher.Rows). Once the reader is done and the process has
/// ended, the exit goes out as the last row, so the last output always goes first (terminal#7).
/// </summary>
internal sealed class RunningTerminal : IAsyncDisposable
{
    private const int ReadSize = 64 * 1024;

    private readonly IPty _pty;
    private readonly ITopics _topics;
    private readonly OutputRing _ring = new();
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private TerminalInfo _info;

    public RunningTerminal(TerminalInfo info, IPty pty, ITopics topics)
    {
        _info = info;
        _pty = pty;
        _topics = topics;
        _ = Task.Run(ReadAsync);
    }

    public TerminalInfo Info
    {
        get
        {
            lock (_lock)
                return _info;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (Info.ExitCode is not null || _stop.IsCancellationRequested)
            throw new CallFailed("the terminal has ended");
        await _pty.WriteAsync(bytes, cancellationToken);
    }

    public void Resize(int cols, int rows)
    {
        lock (_lock)
            _info = _info with { Cols = cols, Rows = rows };
        _pty.Resize(cols, rows);
    }

    public OutputPiece Read(long offset)
    {
        lock (_lock)
            return _ring.Read(offset);
    }

    /// <summary>
    /// The <c>terminal</c> topic's state for a sub resuming after <paramref name="after"/>: what came since, in one row,
    /// when it's kept and at most a read's worth; otherwise the end, and the window reads the replay with
    /// <c>terminal/read</c> (terminal#5). Then the exit, once there is one.
    /// </summary>
    public (long Seq, IReadOnlyList<string> Rows) State(long? after)
    {
        lock (_lock)
        {
            var end = _ring.End;
            var from = after is { } offset && offset >= _ring.Start && offset <= end && end - offset <= OutputRing.ReadLimit ? offset : end;
            List<string> rows = [];
            if (from < end)
                rows.Add(Row(new OutputRow(end, from, _ring.Read(from).Data)));
            if (_info.ExitCode is { } code)
                rows.Add(Row(new OutputRow(end + 1, end, Exit: code)));
            return (from, rows);
        }
    }

    /// <summary>Ends the process group, stops the reader and lets go of the output.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _pty.DisposeAsync();
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[ReadSize];
        try
        {
            int read;
            while ((read = await _pty.ReadAsync(buffer, _stop.Token)) > 0)
            {
                long from;
                lock (_lock)
                {
                    from = _ring.End;
                    _ring.Append(buffer.AsSpan(0, read));
                }
                Publish(new OutputRow(from + read, from, buffer[..read]));
            }
            var code = await _pty.Exited.WaitAsync(_stop.Token);
            long end;
            lock (_lock)
            {
                _info = _info with { ExitCode = code };
                end = _ring.End;
            }
            Publish(new OutputRow(end + 1, end, Exit: code));
        }
        catch (OperationCanceledException)
        {
            // Closed: nobody is left to tell.
        }
    }

    private void Publish(OutputRow row) => _topics.Publish(TerminalFeature.OutputTopic, _info.Id, Row(row));

    private static string Row(OutputRow row) => System.Text.Json.JsonSerializer.Serialize(row, TerminalJson.Default.OutputRow);
}
