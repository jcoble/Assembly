namespace Assembly.Domain.Events;

/// <summary>A message one session sent another (the subagents RFD's <c>session_message</c>): a child's prompt.</summary>
public sealed record SessionMessage(long At, string? MessageId, string Text) : AgentEvent(At);
