namespace Assembly.Domain.Events;

/// <summary>The agent took a message into the turn it was running.</summary>
public sealed record TurnSteered(long At, string Text) : AgentEvent(At);
