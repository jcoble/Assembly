namespace Assembly.Domain.Events;

/// <summary>
/// Provider-owned child display metadata and foreground state. Events contain resolved values:
/// null metadata clears it and unknown state does not confirm activity. CanCancel records the provider's
/// advertised capability, not the presence of an Assembly child-cancel handler.
/// </summary>
public sealed record SubagentChanged(long At, string AcpSessionId, string? Title, string? Description, string? State,
    string? StopReason, string? Error, bool? CanCancel) : AgentEvent(At);
