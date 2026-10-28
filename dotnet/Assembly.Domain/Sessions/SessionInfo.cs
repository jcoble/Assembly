namespace Assembly.Domain.Sessions;

/// <summary>
/// A session in the sessions list. <c>AcpSessionId</c> is the agent's own id for it, kept so a new adapter
/// process can resume the agent's transcript. <c>Machine</c> names the remote machine its agent runs on, or is
/// null when it runs here. <c>SessionCost</c> is the session's total cost as its agent reported it at the last
/// clean close: a resumed session reports on from there. <c>ParentSessionId</c> is set on a subagent's session: the
/// session whose agent runs it. <c>Helper</c> marks a child that is a helper session of its own, which the parent's
/// agent started through the Assembly MCP server, rather than a subagent inside the parent's session. <c>ReadSeq</c> is the
/// seq of the newest row a window has shown, which marks the session read up to there. Times are Unix
/// milliseconds. <c>CompletedAt</c> and <c>SettledAt</c> keep the user's completion and settlement times.
/// <c>TitleSource</c> says where the title came from: <c>prompt</c> or null (the first message, or the agent),
/// <c>helper</c> (the utility model) or <c>user</c>; a <c>helper</c> or <c>user</c> title is kept over a later prompt one.
/// </summary>
public sealed record SessionInfo(string SessionId, string Agent, string RepoUrl, string? Title, string? AcpSessionId, long UpdatedAt,
    string? Machine = null, decimal? SessionCost = null, string? ParentSessionId = null, bool Helper = false, long ReadSeq = 0,
    long? CompletedAt = null, long? SettledAt = null, string? TitleSource = null);
