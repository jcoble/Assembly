using System.Text.Json;
using Assembly.Application.Common.Database;
using System.Threading.Channels;
using Assembly.Domain.Rows;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows.Copies;
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
    private readonly DownloadWaits _downloads;

    /// <param name="queueLimit">Rows a socket may fall behind before it's closed.</param>
    /// <param name="topics">Where each saved row is also published, on the <c>rows</c> topic under its session.</param>
    /// <param name="summaries">The sessions' summaries, which every saved row updates.</param>
    /// <param name="summaryChanged">Told, under the hub's lock, the session whose summary a row changed.</param>
    /// <param name="time">The clock a page waiting on a download is bounded by.</param>
    public LiveHub(RowStore store, int queueLimit = 4096, ITopics? topics = null, SessionSummaries? summaries = null,
        Action<string>? summaryChanged = null, TimeProvider? time = null)
    {
        _store = store;
        _downloads = new DownloadWaits(time ?? TimeProvider.System);
        _queueLimit = queueLimit;
        _topics = topics;
        _summaries = summaries;
        _summaryChanged = summaryChanged ?? (_ => { });
        if (_topics is not null)
            _topics.Unsubscribed += RowsUnsubscribed;
    }

    /// <summary>Commits the batch, then queues its saved rows for the session's sockets. Returns them as saved.</summary>
    public IReadOnlyList<StoredRow> Publish(string sessionId, RowBatch batch, Action<IWriteScope>? observe = null)
    {
        if (batch.IsEmpty && observe is null)
            return [];
        IReadOnlyList<StoredRow> saved;
        lock (_gate)
        {
            saved = _store.Commit(sessionId, batch, observe);
            if (saved.Count > 0) Push(sessionId, saved);
        }
        if (saved.Count > 0) Committed?.Invoke(sessionId, saved);
        return saved;
    }

    public IReadOnlyList<StoredRow> PublishRecovered(RecoveredChatTurn recovered, RowBatch batch, Action<IWriteScope>? observe)
    {
        lock (_gate)
        {
            var saved = _store.CommitRecovered(recovered, batch, observe);
            if (saved.Count > 0) Push(recovered.SessionId, saved);
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

    /// <summary>Which remote sessions here are saved copies. Its saves go through the hub's own doors below.</summary>
    public CopyRecords Copies => _store.Copies;

    /// <summary>
    /// Saves a download's first chunk (TSK-1692): rows already here are skipped and the next seq never goes back, so a
    /// live row copied meanwhile is saved once. Nothing is pushed: the rows are history above an open window's own,
    /// read by its page calls. The session's summary takes them, as a copied row's does.
    /// </summary>
    public CopyStart StartCopy(string sessionId, string machine, BeforeChunk first)
    {
        var rows = first.Rows.Select(RowWire.Read).ToList();
        CopyStart start;
        lock (_gate)
        {
            start = _store.Copies.Start(sessionId, machine, rows, first.Facts);
            Summarize(sessionId, [.. rows.OrderBy(row => row.Seq)]);
        }
        _downloads.Saved(sessionId);
        return start;
    }

    /// <summary>Saves older rows below the copy's first seq. Nothing is pushed and the summary stays: they're older.</summary>
    public IReadOnlyList<StoredRow> Backfill(string sessionId, IReadOnlyList<StoredRow> rows)
    {
        IReadOnlyList<StoredRow> subagents;
        lock (_gate)
            subagents = _store.Copies.Backfill(sessionId, rows);
        _downloads.Saved(sessionId);
        return subagents;
    }

    /// <summary>A forward copy the follower made becomes a copy still downloading.</summary>
    public void AdoptCopy(string sessionId, string machine, HistoryFacts facts)
    {
        lock (_gate)
            _store.Copies.Adopt(sessionId, machine, facts);
    }

    /// <summary>
    /// The rows topic's word that a session's rows were replaced, not a row: a window showing it reloads its page.
    /// </summary>
    public const string Reset = """{"reset":0}""";

    /// <summary>Rows imported on the remote below the copy's oldest: the copy downloads again below it.</summary>
    public void ReopenCopy(string sessionId, HistoryFacts facts)
    {
        lock (_gate)
            _store.Copies.Reopen(sessionId, facts);
    }

    /// <summary>
    /// Deletes the copy and its rows, in one write, so it downloads again from the top. Its summary goes with them,
    /// and a window showing it is told to reload.
    /// </summary>
    public void DropCopy(string sessionId)
    {
        lock (_gate)
        {
            _store.Copies.Drop(sessionId);
            if (_summaries?.Remove(sessionId) == true) _summaryChanged(sessionId);
            _topics?.Publish(RowsFeature.Topic, sessionId, Reset);
        }
    }

    public void CompleteCopy(string sessionId)
    {
        lock (_gate)
            _store.Copies.MarkComplete(sessionId);
    }

    /// <summary>A download of the session runs from now until <see cref="EndDownload"/>; pages below its copy wait on it.</summary>
    public void BeginDownload(string sessionId) => _downloads.Begin(sessionId);

    /// <summary>The download ended, whole or cut. Every page waiting on it reads again at once.</summary>
    public void EndDownload(string sessionId) => _downloads.End(sessionId);

    /// <summary>
    /// Raised when nobody watches the session's rows any more, by live socket or rows sub, whichever went last; sometimes
    /// under the hub's lock, so a handler only signals.
    /// </summary>
    public event Action<string>? Unwatched;

    /// <summary>Rows this machine produced were saved, outside the hub's lock. A row copied from another machine isn't one.</summary>
    public event Action<string, IReadOnlyList<StoredRow>>? Committed;

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

    /// <summary>
    /// A page, as <see cref="Page"/> reads it, of a session that may be a saved copy still downloading (TSK-1692). Such
    /// a page says there is more history while the copy isn't whole. A page with no rows below a running download waits
    /// for its next chunk, its end, or 10 s, then reads again; with no download running it answers at once.
    /// </summary>
    public async Task<RowPage> PageAsync(string sessionId, long before, int limit, CancellationToken ct)
    {
        // The signal is taken before the read, so a chunk saved between the two still wakes the wait.
        var next = _downloads.Next(sessionId);
        var page = Page(sessionId, before, limit, ct);
        if (_store.Copies.RemoteCopyOf(sessionId) is not { Complete: false }) return page;
        if (page.Rows.Count > 0 || next is null) return page with { HasBefore = true };
        await _downloads.WaitAsync(next, ct);
        page = Page(sessionId, before, limit, ct);
        return page with { HasBefore = page.HasBefore || _store.Copies.RemoteCopyOf(sessionId) is { Complete: false } };
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

    internal FinalizedChatReply? TerminalReply(string sessionId, string runId)
    {
        lock (_gate) return _store.TerminalReply(sessionId, runId);
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

    internal UserDeliveryRow? UserDeliveryOfClient(string sessionId, string clientId)
    {
        lock (_gate) return _store.UserDeliveryOfClient(sessionId, clientId);
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
        Summarize(sessionId, rows);
    }

    private void Summarize(string sessionId, IReadOnlyList<StoredRow> rows)
    {
        var summaryChanged = _summaries?.Apply(sessionId, rows) == true;
        // Every committed row that can move a session between the rail's tabs, change which ask it shows or its
        // branch tells the rail, whether or not the summary changed: the open ask it shows can change while one stays open.
        if (_topics is not null && rows.Any(row => row.Kind is RowKind.Turn or RowKind.Ask or RowKind.Task or RowKind.Subagent or RowKind.Stats))
            _topics.Publish(SessionsFeature.RailTopic, sessionId,
                JsonSerializer.Serialize(new SessionsFeature.RailChange(sessionId), SessionsJson.Default.RailChange));
        // Last, so the rows have gone out whatever the item's write does. Only a row that changes the session's
        // summary sends its list item again, never one per row.
        if (summaryChanged)
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
