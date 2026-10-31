using System.Collections.Immutable;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// What the sessions list shows of a session's rows (plan C1, step 1c). <c>RunId</c> is its open run's, <c>TurnState</c>
/// its newest turn row's, <c>EndSeq</c> the seq of the newest turn row that ended a turn, <c>PendingAsk</c> whether an
/// ask is open, and <c>Background</c> how many tasks and subagents are at work.
/// </summary>
public sealed record SessionSummary(string? RunId, string? TurnState, long EndSeq, bool PendingAsk, int Background)
{
    /// <summary>A session with no rows.</summary>
    public static readonly SessionSummary None = new(null, null, 0, false, 0);

    /// <summary><c>waiting</c> on an open ask, else <c>running</c> with a run open, else <c>failed</c> if the newest turn failed, else <c>idle</c>.</summary>
    public string State => PendingAsk ? "waiting" : RunId is not null ? "running" : TurnState == "failed" ? "failed" : "idle";
}

/// <summary>
/// Each session's <see cref="SessionSummary"/>, kept in memory: filled once at the start from one grouped query, plus
/// a look back for each session whose newest turn row didn't end a turn, then changed by <see cref="LiveHub"/> as it
/// saves each row, under its lock. Readers get the current snapshot, swapped whole on each change, so a reader under
/// another lock (the sessions topic's state, under the Topics lock) takes none.
/// </summary>
public sealed class SessionSummaries
{
    // Each session's rows as far as they decide its summary. Only the start and the hub's lock touch them.
    private readonly Dictionary<string, Tracker> _trackers = [];
    private ImmutableDictionary<string, SessionSummary> _current = ImmutableDictionary<string, SessionSummary>.Empty;

    private SessionSummaries()
    {
    }

    public static SessionSummaries Load(RowStore store)
    {
        var summaries = new SessionSummaries();
        foreach (var row in store.SummaryRows())
            summaries.Track(row);
        // A newest turn row that ended a turn gave its seq; any other looks back for the turn before it that ended.
        foreach (var (sessionId, tracker) in summaries._trackers)
            if (tracker.TurnState is not null && !Ends(tracker.TurnState))
                tracker.EndSeq = store.LastTurnEnd(sessionId);
        summaries._current = summaries._trackers.ToImmutableDictionary(entry => entry.Key, entry => entry.Value.Summary());
        return summaries;
    }

    public SessionSummary Get(string sessionId) => Volatile.Read(ref _current).GetValueOrDefault(sessionId) ?? SessionSummary.None;

    /// <summary>Whether any session has a run open: what holds App Nap.</summary>
    public bool AnyRunOpen => Volatile.Read(ref _current).Values.Any(summary => summary.RunId is not null);

    /// <summary>Takes in the session's newly saved rows. True if its summary changed. Only under the hub's lock.</summary>
    internal bool Apply(string sessionId, IReadOnlyList<StoredRow> rows)
    {
        var counted = false;
        foreach (var row in rows.Where(Counts))
        {
            Track(row);
            counted = true;
        }
        if (!counted)
            return false;
        var summary = _trackers[sessionId].Summary();
        if (summary == Get(sessionId))
            return false;
        ImmutableInterlocked.AddOrUpdate(ref _current, sessionId, summary, (_, _) => summary);
        return true;
    }

    /// <summary>Uses the remote machine's current state even when its rows aren't being followed here.</summary>
    public void ApplyRemote(Sessions.SessionItem item) =>
        ImmutableInterlocked.AddOrUpdate(ref _current, item.SessionId,
            new SessionSummary(item.ActiveRunId, item.State == "failed" ? "failed" : null,
                Get(item.SessionId).EndSeq, item.PendingAsk, item.Background), (_, old) =>
                new(item.ActiveRunId, item.State == "failed" ? "failed" : null, old.EndSeq, item.PendingAsk, item.Background));

    // Deltas, messages, tools and the rest never change a summary.
    private static bool Counts(StoredRow row) => row.Kind is RowKind.Turn or RowKind.Ask or RowKind.Task or RowKind.Subagent;

    private static bool Ends(string? turnState) => turnState is "finished" or "failed" or "cancelled";

    private void Track(StoredRow row)
    {
        var tracker = TrackerOf(row.SessionId);
        var state = row.Payload.TryGetProperty("state", out var value) ? value.GetString() : null;
        switch (row.Kind)
        {
            case RowKind.Turn:
                tracker.RunId = state == "started" ? row.RunId : null;
                tracker.TurnState = state;
                if (Ends(state))
                    tracker.EndSeq = row.Seq;
                break;
            case RowKind.Ask when state == "open":
                tracker.Asks.Add(row.ItemId ?? "");
                break;
            case RowKind.Ask:
                tracker.Asks.Remove(row.ItemId ?? "");
                break;
            case RowKind.Task or RowKind.Subagent when state is "running" or "requires_action":
                tracker.Working.Add($"{row.Kind} {row.ItemId}");
                break;
            case RowKind.Task or RowKind.Subagent:
                tracker.Working.Remove($"{row.Kind} {row.ItemId}");
                break;
        }
    }

    private Tracker TrackerOf(string sessionId)
    {
        if (!_trackers.TryGetValue(sessionId, out var tracker))
            _trackers[sessionId] = tracker = new Tracker();
        return tracker;
    }

    private sealed class Tracker
    {
        public string? RunId { get; set; }
        public string? TurnState { get; set; }
        public long EndSeq { get; set; }

        /// <summary>The open asks, by item.</summary>
        public HashSet<string> Asks { get; } = [];

        /// <summary>The tasks and subagents at work, by kind and item.</summary>
        public HashSet<string> Working { get; } = [];

        public SessionSummary Summary() => new(RunId, TurnState, EndSeq, Asks.Count > 0, Working.Count);
    }
}
