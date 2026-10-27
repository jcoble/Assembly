namespace Assembly.Domain.Events;

/// <summary>
/// The agent took a message into turn <c>RunId</c>, which may have ended by the time it said so. <c>ClientId</c> is
/// the sender's id for it.
/// </summary>
public sealed record TurnSteered(long At, string RunId, string Text, string? ClientId = null) : AgentEvent(At);
