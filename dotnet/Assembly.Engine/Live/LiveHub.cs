using System.Threading.Channels;
using Assembly.Engine.Rows;
using Assembly.Engine.Store;

namespace Assembly.Engine.Live;

/// <summary>
/// The row store's one door. Every commit, and every socket's backlog read, happens under one lock, so a row
/// reaches a socket only after it's saved, and a socket that opens mid-turn gets each row exactly once:
/// either in its backlog or on its queue, never both.
/// </summary>
public sealed class LiveHub : IDisposable
{
    private readonly RowStore _store;
    private readonly int _queueLimit;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<LiveSubscription>> _subscribers = [];

    /// <param name="queueLimit">Rows a socket may fall behind before it's closed.</param>
    public LiveHub(RowStore store, int queueLimit = 4096)
    {
        _store = store;
        _queueLimit = queueLimit;
    }

    /// <summary>Commits the batch, then queues its saved rows for the thread's sockets. Returns them as saved.</summary>
    public IReadOnlyList<StoredRow> Publish(string threadId, RowBatch batch)
    {
        if (batch.IsEmpty)
            return [];
        lock (_gate)
        {
            var saved = _store.Commit(threadId, batch);
            Push(threadId, saved);
            return saved;
        }
    }

    /// <summary>Saves a row another machine committed, under its seq, then queues it like a commit. False if it isn't the next one.</summary>
    public bool PublishCopy(StoredRow row)
    {
        lock (_gate)
        {
            if (!_store.Copy(row))
                return false;
            Push(row.ThreadId, [row]);
            return true;
        }
    }

    /// <summary>Whether a webview has the thread's live socket open.</summary>
    public bool HasSubscribers(string threadId)
    {
        lock (_gate)
            return _subscribers.TryGetValue(threadId, out var subscribers) && subscribers.Count > 0;
    }

    /// <summary>The rows after <paramref name="after"/>, and from then on every new row of the thread.</summary>
    public LiveSubscription Subscribe(string threadId, long after)
    {
        var queue = Channel.CreateBounded<StoredRow>(new BoundedChannelOptions(_queueLimit) { SingleReader = true, SingleWriter = true });
        lock (_gate)
        {
            var subscription = new LiveSubscription(threadId, _store.RowsAfter(threadId, after), queue, subscriber => Close(subscriber, null));
            if (!_subscribers.TryGetValue(threadId, out var subscribers))
                _subscribers[threadId] = subscribers = [];
            subscribers.Add(subscription);
            return subscription;
        }
    }

    public RowPage Page(string threadId, long before, int limit)
    {
        lock (_gate)
            return _store.PageBefore(threadId, before, limit);
    }

    public StoredToolCall? ToolCall(string threadId, string itemId)
    {
        lock (_gate)
            return _store.ToolCall(threadId, itemId);
    }

    public void SaveToolDetail(string threadId, ToolDetail detail)
    {
        lock (_gate)
            _store.SaveToolDetail(threadId, detail);
    }

    /// <summary>The rows of one run, oldest first.</summary>
    public IReadOnlyList<StoredRow> RunRows(string threadId, string runId)
    {
        lock (_gate)
            return _store.RunRows(threadId, runId);
    }

    public long LastSeq(string threadId)
    {
        lock (_gate)
            return _store.LastSeq(threadId);
    }

    public string? OpenRun(string threadId)
    {
        lock (_gate)
            return _store.OpenRun(threadId);
    }

    public bool RunEnded(string threadId, string runId)
    {
        lock (_gate)
            return _store.RunEnded(threadId, runId);
    }

    public string? ThreadOfRun(string runId)
    {
        lock (_gate)
            return _store.ThreadOfRun(runId);
    }

    /// <summary>Ends every subscription; their sockets close, and the webview reopens them from its watermark.</summary>
    public void CloseSubscriptions()
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers.Values.SelectMany(list => list).ToList())
                Close(subscriber, null);
        }
    }

    /// <summary>Ends every subscription. The store belongs to the caller.</summary>
    public void Dispose() => CloseSubscriptions();

    private void Push(string threadId, IReadOnlyList<StoredRow> rows)
    {
        if (!_subscribers.TryGetValue(threadId, out var subscribers))
            return;
        foreach (var subscriber in subscribers.ToList())
        {
            if (!rows.All(subscriber.Queue.Writer.TryWrite))
                Close(subscriber, new LiveFellBehind());
        }
    }

    private void Close(LiveSubscription subscriber, Exception? reason)
    {
        lock (_gate)
        {
            subscriber.Queue.Writer.TryComplete(reason);
            if (_subscribers.TryGetValue(subscriber.ThreadId, out var subscribers) && subscribers.Remove(subscriber) && subscribers.Count == 0)
                _subscribers.Remove(subscriber.ThreadId);
        }
    }
}
