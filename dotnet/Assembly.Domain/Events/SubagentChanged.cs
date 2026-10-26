namespace Assembly.Domain.Events;

/// <summary>
/// The agent announced a subagent as a child session, or its work state changed (the subagents RFD's
/// <c>subagent_update</c>). <c>State</c> is <c>running</c>, <c>requires_action</c> or <c>idle</c>; an idle child says
/// why in <c>StopReason</c> (<c>end_turn</c>, <c>cancelled</c>, <c>error</c>) and <c>Error</c>. Only the first update
/// carries the title and description. <c>CanCancel</c> when the child stops on <c>session/cancel</c> with its id.
/// </summary>
public sealed record SubagentChanged(long At, string AcpSessionId, string? Title, string? Description, string? State,
    string? StopReason, string? Error, bool? CanCancel) : AgentEvent(At);
