namespace Assembly.Domain.Events;

/// <summary>The agent took a message into the turn it was running. <c>ClientId</c> is the sender's id for it.</summary>
public sealed record TurnSteered(long At, string Text, string? ClientId = null) : AgentEvent(At);
