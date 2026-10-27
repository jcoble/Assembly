using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>A session as the sessions topic and <c>GET /sessions</c> show it.</summary>
public sealed record SessionItem(string SessionId, string Agent, string RepoUrl, string? Title, string? Machine, string? ParentSessionId,
    string? AcpSessionId, long UpdatedAt)
{
    public static SessionItem From(SessionInfo session) =>
        new(session.SessionId, session.Agent, session.RepoUrl, session.Title, session.Machine, session.ParentSessionId, session.AcpSessionId,
            session.UpdatedAt);
}
