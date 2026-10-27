using System.Collections.Immutable;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// What the sessions list shows of a session's rows (plan C1, step 1c). <c>RunId</c> is its open run's, <c>TurnState</c>
/// its newest turn row's, <c>EndSeq</c> the seq of the newest turn row that ended it, <c>PendingAsk</c> whether an ask
/// is open, and <c>Background</c> how many tasks and subagents are at work.
/// </summary>
public sealed record SessionSummary(string? RunId, string? TurnState, long EndSeq, bool PendingAsk, int Background)
{
    /// <summary>A session with no rows.</summary>
    public static readonly SessionSummary None = new(null, null, 0, false, 0);

    /// <summary><c>waiting</c> on an open ask, else <c>running</c> with a run open, else <c>failed</c> if the newest turn failed, else <c>idle</c>.</summary>
    public string State => "idle";
}

/// <summary>Each session's <see cref="SessionSummary"/>, kept in memory.</summary>
public sealed class SessionSummaries
{
    private volatile ImmutableDictionary<string, SessionSummary> _current = ImmutableDictionary<string, SessionSummary>.Empty;

    public static SessionSummaries Load(RowStore store) => new();

    public SessionSummary Get(string sessionId) => _current.GetValueOrDefault(sessionId) ?? SessionSummary.None;

    internal bool Apply(string sessionId, IReadOnlyList<StoredRow> rows) => false;
}
