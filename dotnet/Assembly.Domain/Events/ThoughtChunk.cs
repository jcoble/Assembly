namespace Assembly.Domain.Events;

/// <summary>More of the agent's thinking.</summary>
public sealed record ThoughtChunk(long At, string? MessageId, string Text, bool Replace = false) : AgentEvent(At);
