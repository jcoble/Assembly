using System.Text.Json;
using System.Threading.Channels;
using Assembly.Domain.Rows;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// The row store's one door. Every commit, and every socket's backlog read, happens under one lock, so a row
/// reaches a socket only after it's saved, and a socket that opens mid-turn gets each row exactly once:
/// either in its backlog or on its queue, never both.
/// </summary>
public sealed class LiveHub : IDisposable
{
    private readonly RowStore _store;
    private readonly int _queueLimit;
    private readonly ITopics? _topics;
    private readonly SessionSummaries? _summaries;
    private readonly Action<string> _summaryChanged;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<LiveSubscription>> _subscribers = [];

    /// <param name="queueLimit">Rows a socket may fall behind before it's closed.</param>
    /// <param name="topics">Where each saved row is also published, on the <c>rows</c> topic under its session.</param>
    /// <param name="summaries">The sessions' summaries, which every saved row updates.</param>
    /// <param name="summaryChanged">Told, under the hub's lock, the session whose summary a row changed.</param>
    public LiveHub(RowStore store, int queueLimit = 4096, ITopics? topics = null, SessionSummaries? summaries = null,
        Action<string>? summaryChanged = null)
    {
        _store = store;
        _queueLimit = queueLimit;
        _topics = topics;
        _summaries = summaries;
        _summaryChanged = summaryChanged ?? (_ => { });
        if (_topics is not null)
            _topics.Unsubscribed += RowsUnsubscribed;
    }

    /// <summary>Commits the batch, then queues its saved rows for the session's sockets. Returns them as saved.</summary>
    public IReadOnlyList<StoredRow> Publish(string sessionId, RowBatch batch)
    {
        if (batch.IsEmpty)
            return [];
        lock (_gate)
        {
            var saved = _store.Commit(sessionId, batch);
            Push(sessionId, saved);
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
            Push(row.SessionId, [row]);
            return true;
        }
    }

    /// <summary>
    /// Raised when nobody watches the session's rows any more, by live socket or rows sub, whichever went last; sometimes
    /// under the hub's lock, so a handler only signals.
    /// </summary>
    public event Action<string>? Unwatched;

    /// <summary>Whether a webview watches the session's rows: its live socket, or a rows sub on the socket.</summary>
    public bool HasSubscribers(string sessionId)
    {
        lock (_gate)
            return (_subscribers.TryGetValue(sessionId, out var subscribers) && subscribers.Count > 0)
                || _topics?.HasSubscribers(RowsFeature.Topic, sessionId) == true;
    }

    /// <summary>The rows after <paramref name="after"/>, and from then on every new row of the session.</summary>
    public LiveSubscription Subscribe(string sessionId, long after)
    {
        var queue = Channel.CreateBounded<StoredRow>(new BoundedChannelOptions(_queueLimit) { SingleReader = true, SingleWriter = true });
        lock (_gate)
        {
            var subscription = new LiveSubscription(sessionId, _store.RowsAfter(sessionId, after), queue, subscriber => Close(subscriber, null));
            if (!_subscribers.TryGetValue(sessionId, out var subscribers))
                _subscribers[sessionId] = subscribers = [];
            subscribers.Add(subscription);
            return subscription;
        }
    }

    public RowPage Page(string sessionId, long before, int limit, CancellationToken cancel = default)
    {
        lock (_gate)
            return _store.PageBefore(sessionId, before, limit, cancel);
    }

    public StoredToolCall? ToolCall(string sessionId, string itemId)
    {
        lock (_gate)
            return _store.ToolCall(sessionId, itemId);
    }

    public void SaveToolDetail(string sessionId, ToolDetail detail)
    {
        lock (_gate)
            _store.SaveToolDetail(sessionId, detail);
    }

    /// <summary>The rows of one run, oldest first.</summary>
    public IReadOnlyList<StoredRow> RunRows(string sessionId, string runId)
    {
        lock (_gate)
            return _store.RunRows(sessionId, runId);
    }

    public long LastSeq(string sessionId)
    {
        lock (_gate)
            return _store.LastSeq(sessionId);
    }

    public string? OpenRun(string sessionId)
    {
        lock (_gate)
            return _store.OpenRun(sessionId);
    }

    public long LastTurnEnd(string sessionId)
    {
        lock (_gate)
            return _store.LastTurnEnd(sessionId);
    }

    public (string? RunId, string Text)? UserRowOfClient(string sessionId, string clientId)
    {
        lock (_gate)
            return _store.UserRowOfClient(sessionId, clientId);
    }

    public bool RunEnded(string sessionId, string runId)
    {
        lock (_gate)
            return _store.RunEnded(sessionId, runId);
    }

    public string? SessionOfRun(string runId)
    {
        lock (_gate)
            return _store.SessionOfRun(runId);
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
    public void Dispose()
    {
        if (_topics is not null)
            _topics.Unsubscribed -= RowsUnsubscribed;
        CloseSubscriptions();
    }

    private void RowsUnsubscribed(string topic, string? key)
    {
        if (topic == RowsFeature.Topic && key is not null)
            UnwatchedIfNobody(key);
    }

    private void UnwatchedIfNobody(string sessionId)
    {
        if (!HasSubscribers(sessionId))
            Unwatched?.Invoke(sessionId);
    }

    private void Push(string sessionId, IReadOnlyList<StoredRow> rows)
    {
        // Deltas are the hottest path: a session nobody watches on rows isn't serialized.
        if (_topics is not null && _topics.HasSubscribers(RowsFeature.Topic, sessionId))
            foreach (var row in rows)
                _topics.Publish(RowsFeature.Topic, sessionId, RowWire.ToJson(row));
        if (_subscribers.TryGetValue(sessionId, out var subscribers))
            foreach (var subscriber in subscribers.ToList())
            {
                if (!rows.All(subscriber.Queue.Writer.TryWrite))
                    Close(subscriber, new LiveFellBehind());
            }
        // Every committed row that can move a session between the rail's tabs, change which ask it shows or its
        // branch tells the rail, whether or not the summary changed: the open ask it shows can change while one stays open.
        if (_topics is not null && rows.Any(row => row.Kind is RowKind.Turn or RowKind.Ask or RowKind.Task or RowKind.Subagent or RowKind.Stats))
            _topics.Publish(SessionsFeature.RailTopic, sessionId,
                JsonSerializer.Serialize(new SessionsFeature.RailChange(sessionId), SessionsJson.Default.RailChange));
        // Last, so the rows have gone out whatever the item's write does. Only a row that changes the session's
        // summary sends its list item again, never one per row.
        if (_summaries?.Apply(sessionId, rows) == true)
            _summaryChanged(sessionId);
    }

    private void Close(LiveSubscription subscriber, Exception? reason)
    {
        var last = false;
        lock (_gate)
        {
            subscriber.Queue.Writer.TryComplete(reason);
            if (_subscribers.TryGetValue(subscriber.SessionId, out var subscribers) && subscribers.Remove(subscriber) && subscribers.Count == 0)
                last = _subscribers.Remove(subscriber.SessionId);
        }
        if (last)
            UnwatchedIfNobody(subscriber.SessionId);
    }
}
