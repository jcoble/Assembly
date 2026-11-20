namespace Assembly.Domain.Events;

/// <summary>The agent took steer <c>SteerId</c> into the session at this point in its stream.</summary>
public sealed record SteerInserted(long At, string SteerId) : AgentEvent(At);
