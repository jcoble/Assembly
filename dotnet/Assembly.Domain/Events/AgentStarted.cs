namespace Assembly.Domain.Events;

/// <summary>
/// The adapter opened the session: a new one, or the thread's earlier one, resumed. A resumed session's agent
/// reports its cost on from <c>SavedCost</c>, the total it reported at its last clean close, when that is known.
/// <c>Model</c> is the model its config options name, if they name one.
/// </summary>
public sealed record AgentStarted(long At, bool Resumed, decimal? SavedCost = null, string? Model = null) : AgentEvent(At);
