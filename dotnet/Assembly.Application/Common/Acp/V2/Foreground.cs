using System.Text.Json;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Acp.V2;

/// <summary>Tracks insertion and foreground boundaries in pipe order, independently of response scheduling.</summary>
internal sealed class Foreground : IDisposable
{
    private sealed class Boundary
    {
        public bool Acknowledged;
        public bool Active;
        public StateUpdate? End;
    }

    internal sealed class Pending
    {
        public string? MessageId;
        public TaskCompletionSource<string> Inserted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PromptResult> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _gate = new();
    private readonly List<Pending> _pending = [];
    private readonly Dictionary<string, Boundary> _messages = [];
    private readonly List<TaskCompletionSource> _cancels = [];
    private CancellationTokenSource _work = new();
    private Exception? _closed;
    private bool _active;
    private bool _cancelRequested;
    private bool _disposed;

    public CancellationToken Work
    {
        get { lock (_gate) return _work.Token; }
    }

    public bool Running
    {
        get { lock (_gate) return _active || _pending.Count > 0; }
    }

    public Pending Begin()
    {
        lock (_gate)
        {
            if (_closed is not null) throw new IOException("The ACP session is closed.", _closed);
            if (_work.IsCancellationRequested)
            {
                _work.Dispose();
                _work = new CancellationTokenSource();
            }
            var pending = new Pending();
            _pending.Add(pending);
            return pending;
        }
    }

    public void Inserted(Pending pending, string messageId)
    {
        lock (_gate)
        {
            if (!_pending.Contains(pending)) return;
            pending.MessageId = messageId;
            pending.Inserted.TrySetResult(messageId);
            _messages.TryAdd(messageId, new Boundary());
            Complete();
        }
        EndWorkIfIdle();
    }

    public void Failed(Pending pending, Exception error)
    {
        lock (_gate)
        {
            _pending.Remove(pending);
            pending.Inserted.TrySetException(error);
            pending.Ended.TrySetException(error);
            Prune();
        }
        EndWorkIfIdle();
    }

    public void Read(SessionNotification notification, Action? publishAccepted = null)
    {
        lock (_gate)
        {
            if (_closed is not null) return;
            var update = notification.Update;
            if (notification.Kind is "user_message" or "user_message_chunk"
                && update.TryGetProperty("messageId", out var id) && id.GetString() is { Length: > 0 } messageId
                && _pending.Count > 0)
            {
                if (!_messages.TryGetValue(messageId, out var boundary))
                    _messages[messageId] = boundary = new Boundary();
                boundary.Acknowledged = true;
            }
            if (notification.Kind != "state_update")
            {
                publishAccepted?.Invoke();
                return;
            }
            var state = update.Deserialize(AcpJsonContext.Default.StateUpdate);
            if (state is null) return;
            _active = state.State is "running" or "requires_action";
            if (state.State == "idle") _cancelRequested = false;
            if (_active && !_cancelRequested && _work.IsCancellationRequested)
            {
                _work.Dispose();
                _work = new CancellationTokenSource();
            }
            foreach (var boundary in _messages.Values.Where(b => b.Acknowledged && b.End is null))
            {
                if (state.State is "running" or "requires_action") boundary.Active = true;
                else if (state.State == "idle" && (boundary.Active || state.StopReason is not null)) boundary.End = state;
            }
            publishAccepted?.Invoke();
            if (state is { State: "idle", StopReason: "cancelled" })
            {
                foreach (var cancel in _cancels) cancel.TrySetResult();
                _cancels.Clear();
            }
            Complete();
            if (state.State == "idle") _work.Cancel();
        }
        EndWorkIfIdle();
    }

    public TaskCompletionSource Cancelling()
    {
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _cancelRequested = true;
            if (_closed is not null) waiting.TrySetException(_closed);
            else
            {
                _cancels.Add(waiting);
                _work.Cancel();
            }
        }
        return waiting;
    }

    public void RemoveCancel(TaskCompletionSource waiting)
    {
        lock (_gate) _cancels.Remove(waiting);
    }

    public void Close(Exception error)
    {
        lock (_gate)
        {
            if (_closed is not null) return;
            _closed = error;
            foreach (var pending in _pending)
            {
                pending.Inserted.TrySetException(error);
                pending.Ended.TrySetException(error);
            }
            foreach (var cancel in _cancels) cancel.TrySetException(error);
            _pending.Clear(); _messages.Clear(); _cancels.Clear();
            _work.Cancel();
        }
    }

    private void Complete()
    {
        foreach (var pending in _pending.ToArray())
        {
            if (pending.MessageId is not { } id || !_messages.TryGetValue(id, out var boundary) || boundary.End is not { } end)
                continue;
            pending.Ended.TrySetResult(new PromptResult(id, end.StopReason ?? "end_turn", end.Error, end.ReportedUsage));
            _pending.Remove(pending);
        }
        Prune();
    }

    private void Prune()
    {
        // Until every response binds its id, early-completed boundaries may still belong to an outstanding request.
        if (_pending.Any(p => p.MessageId is null)) return;
        foreach (var id in _messages.Keys.Where(id => !_pending.Any(p => p.MessageId == id)).ToArray()) _messages.Remove(id);
    }

    private void EndWorkIfIdle()
    {
        lock (_gate)
        {
            if (_closed is null && !_active && _pending.Count == 0) _work.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Close(new ObjectDisposedException(nameof(Foreground)));
            _work.Dispose();
            _disposed = true;
        }
    }
}
