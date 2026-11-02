using Assembly.Application.Common.Acp.Messages;
using System.Threading.Channels;

namespace Assembly.Remote.Smoke;

/// <summary>Owns the update reader and event waits for one adapter connection.</summary>
public sealed class UpdateWatcher
{
    private readonly ChannelReader<SessionNotification> _updates;
    private readonly Lock _gate = new();
    private readonly List<SessionNotification> _seen = [];
    private readonly List<(Func<SessionNotification, bool> Match, TaskCompletionSource<SessionNotification> Signal)> _waits = [];
    private TurnLog _current = new();
    private bool _closed;

    public UpdateWatcher(ChannelReader<SessionNotification> updates)
    {
        _updates = updates;
        Completion = Task.Run(WatchAsync);
    }

    public Task Completion { get; }

    public TurnLog BeginTurn()
    {
        lock (_gate) return _current = new TurnLog();
    }

    /// <summary>Registers before an action; already received session updates also satisfy the wait.</summary>
    public async Task<SessionNotification> WaitForAsync(Func<SessionNotification, bool> match, CancellationToken cancellationToken)
    {
        var signal = new TaskCompletionSource<SessionNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_seen.FirstOrDefault(match) is { } seen) return seen;
            if (_closed) throw new IOException("The adapter update stream closed before the expected event.");
            _waits.Add((match, signal));
        }
        try { return await signal.Task.WaitAsync(cancellationToken); }
        finally { lock (_gate) _waits.RemoveAll(wait => wait.Signal == signal); }
    }

    /// <summary>Processes updates already queued, under the same gate as the owned reader.</summary>
    public Task DrainAsync()
    {
        lock (_gate) Drain();
        return Task.CompletedTask;
    }

    private void Drain()
    {
        while (_updates.TryRead(out var update))
        {
            _current.Add(update);
            _seen.Add(update);
            foreach (var wait in _waits)
                if (wait.Match(update)) wait.Signal.TrySetResult(update);
        }
    }

    private async Task WatchAsync()
    {
        try
        {
            while (await _updates.WaitToReadAsync())
                lock (_gate) Drain();
        }
        finally
        {
            lock (_gate)
            {
                _closed = true;
                foreach (var wait in _waits)
                    wait.Signal.TrySetException(new IOException("The adapter update stream closed before the expected event."));
                _waits.Clear();
            }
        }
    }
}
