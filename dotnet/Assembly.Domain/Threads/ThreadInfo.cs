namespace Assembly.Domain.Threads;

/// <summary>
/// A thread in the sessions list. <c>AcpSessionId</c> is the agent's own id for it, kept so a new adapter
/// process can resume the agent's transcript. <c>Machine</c> names the remote machine its agent runs on, or is
/// null when it runs here. <c>SessionCost</c> is the session's total cost as its agent reported it at the last
/// clean close: a resumed session reports on from there. <c>ParentThreadId</c> is set on a subagent's thread: the
/// thread whose agent runs it. <c>Helper</c> marks a child that is a helper session of its own, which the parent's
/// agent started through the Assembly MCP server, rather than a subagent inside the parent's session. Times are Unix
/// milliseconds.
/// </summary>
public sealed record ThreadInfo(string ThreadId, string Agent, string RepoUrl, string? Title, string? AcpSessionId, long UpdatedAt,
    string? Machine = null, decimal? SessionCost = null, string? ParentThreadId = null, bool Helper = false);
