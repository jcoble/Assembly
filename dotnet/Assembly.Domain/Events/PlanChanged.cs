namespace Assembly.Domain.Events;

/// <summary>The agent's whole plan, as it stands now.</summary>
public sealed record PlanChanged(long At, IReadOnlyList<PlanEntry> Entries) : AgentEvent(At);
