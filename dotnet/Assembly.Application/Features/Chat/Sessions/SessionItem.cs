using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// A session as the sessions topic shows it: its stored columns, then its summary (step 1c). <c>Unread</c> is a turn
/// that ended past what a window marked read; <c>NeedsAttention</c> is an open ask, or an unread turn with none running.
/// <c>Root</c> is the session's folder on this machine (null when it runs on another), where its checkout is made on its
/// first send.
/// </summary>
public sealed record SessionItem(string SessionId, string Agent, string RepoUrl, string? Title, string? Machine, string? ParentSessionId,
    string? AcpSessionId, long UpdatedAt, string State, string? ActiveRunId, bool PendingAsk, int Background, bool NeedsAttention, bool Unread,
    long? CompletedAt, long? SettledAt, string? Root)
{
    public static SessionItem From(SessionInfo session, SessionSummary summary, string? root)
    {
        var unread = summary.EndSeq > session.ReadSeq;
        return new(session.SessionId, session.Agent, session.RepoUrl, session.Title, session.Machine, session.ParentSessionId, session.AcpSessionId,
            session.UpdatedAt, summary.State, summary.RunId, summary.PendingAsk, summary.Background,
            summary.PendingAsk || (unread && summary.RunId is null), unread, session.CompletedAt, session.SettledAt, root);
    }
}
