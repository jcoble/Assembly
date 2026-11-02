namespace Assembly.Domain.Rows;

/// <summary>
/// A child display row. For provider-native children, <c>SessionId</c> is the Assembly parent;
/// helpers use their own Assembly session. The native child id lives in the row item id. <c>ToolCallId</c> is the
/// parent's tool call that launched it, when its prompt says so. <c>State</c> is <c>running</c>,
/// <c>requires_action</c>, <c>idle</c> or <c>unknown</c>, with <c>StopReason</c> and <c>Error</c> once idle. <c>Agent</c> is the
/// child's agent when it isn't the parent's (a helper session's); <c>Helper</c> says it is one.
/// <c>CanCancel</c> records provider-advertised support; <c>CanStop</c> enables its handler for confirmed current work.
/// </summary>
public sealed record SubagentPayload(string SessionId, string Title, string? Description, string State, string? StopReason,
    string? Error, bool CanStop, string? ToolCallId, string? Agent = null, bool? Helper = null, bool? CanCancel = null);
