namespace Assembly.Engine.Events;

/// <summary>More of the agent's thinking.</summary>
public sealed record ThoughtChunk(long At, string? MessageId, string Text) : AgentEvent(At);
