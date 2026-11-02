using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;
using System.Runtime.CompilerServices;

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
    private readonly Task _reader;
    private TerminalInfo _info;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _writesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writes;
    private bool SignIn => _info.Owner?.StartsWith("signin:", StringComparison.Ordinal) == true;

    public RunningTerminal(TerminalInfo info, IPty pty, ITopics topics)
    {
        _info = info;
        _pty = pty;
        _topics = topics;
        _reader = Task.Run(ReadAsync);
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
        lock (_lock)
        {
            if (_info.ExitCode is not null || _stop.IsCancellationRequested)
                throw new CallFailed(SignIn ? "signin_write_failed" : "the terminal has ended", SignIn ? "signin_write_failed" : null);
            if (_writes++ == 0)
                _writesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            await _pty.WriteAsync(bytes, stop.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stop.IsCancellationRequested) { throw; }
        catch (Exception) when (SignIn)
        {
            throw new CallFailed("signin_write_failed", "signin_write_failed");
        }
        catch (ObjectDisposedException)
        {
            throw new CallFailed("the terminal has ended");
        }
        finally
        {
            lock (_lock)
                if (--_writes == 0)
                    _writesDrained.TrySetResult();
        }
    }

    public Task<int> ExitedAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> OutputAsync([EnumeratorCancellation] CancellationToken ct)
    {
        long offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            OutputPiece piece;
            Task changed;
            bool ended;
            lock (_lock)
            {
                piece = _ring.Read(offset);
                changed = _changed.Task;
                ended = _exit.Task.IsCompleted;
            }
            if (piece.Data.Length != 0)
            {
                offset = piece.Offset + piece.Data.Length;
                yield return piece.Data;
                continue;
            }
            if (ended)
            {
                await _exit.Task.WaitAsync(ct);
                yield break;
            }
            await changed.WaitAsync(ct);
        }
    }

    /// <summary>Does nothing once the program has ended: there's no terminal left to size.</summary>
    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            if (_info.ExitCode is not null)
                return;
            _info = _info with { Cols = cols, Rows = rows };
        }
        try
        {
            _pty.Resize(cols, rows);
        }
        catch (ObjectDisposedException)
        {
            // Ended meanwhile.
        }
    }

    public OutputPiece Read(long offset)
    {
        lock (_lock)
            return _ring.Read(offset);
    }

    /// <summary>Ends the process group, stops the reader and lets go of the output.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync();
            await _pty.DisposeAsync();
        }
        catch (Exception) when (SignIn)
        {
            throw new CallFailed("signin_exit_failed", "signin_exit_failed");
        }
        finally
        {
            await _reader;
            Task writes;
            lock (_lock)
                writes = _writes == 0 ? Task.CompletedTask : _writesDrained.Task;
            await writes;
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[ReadSize];
        var failure = "signin_read_failed";
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
                Changed();
            }
            failure = "signin_exit_failed";
            var code = await _pty.Exited.WaitAsync(_stop.Token);
            long end;
            lock (_lock)
            {
                _info = _info with { ExitCode = code };
                end = _ring.End;
            }
            Publish(new OutputRow(end + 1, end, Exit: code));
            _exit.TrySetResult(code);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            _exit.TrySetCanceled(_stop.Token);
        }
        catch (Exception error)
        {
            // Logged here, so closing it (and the backend quitting) doesn't fail on it.
            _exit.TrySetException(new CallFailed(failure, failure));
            if (!SignIn)
                Console.Error.WriteLine($"terminal {_info.Id}: its output stopped: {error.Message}");
        }
        finally { Changed(); }
    }

    private void Changed()
    {
        lock (_lock)
        {
            _changed.TrySetResult();
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void Publish(OutputRow row) => _topics.Publish(TerminalFeature.OutputTopic, _info.Id, Row(row));

    private static string Row(OutputRow row) => System.Text.Json.JsonSerializer.Serialize(row, TerminalJson.Default.OutputRow);
}
