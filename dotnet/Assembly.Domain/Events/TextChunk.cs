namespace Assembly.Domain.Events;

/// <summary>More of the agent's answer. <c>MessageId</c> is the agent's, when it sends one.</summary>
public sealed record TextChunk(long At, string? MessageId, string Text) : AgentEvent(At);
