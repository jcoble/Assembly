namespace Assembly.Domain.Rows;

/// <summary>
/// A subagent the agent runs as a child session. <c>SessionId</c> is the child's own session. <c>ToolCallId</c> is the
/// parent's tool call that launched it, when its prompt says so. <c>State</c> is <c>running</c>,
/// <c>requires_action</c> or <c>idle</c>, with <c>StopReason</c> and <c>Error</c> once idle. <c>Agent</c> is the
/// child's agent when it isn't the parent's (a helper session's); <c>Helper</c> says it is one.
/// </summary>
public sealed record SubagentPayload(string SessionId, string Title, string? Description, string State, string? StopReason,
    string? Error, bool CanStop, string? ToolCallId, string? Agent = null, bool? Helper = null);
