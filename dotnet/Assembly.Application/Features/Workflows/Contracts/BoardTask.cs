namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board Task contract.</summary>
public sealed record BoardTask(string MachineId, string TaskKey, string CreatorSessionId, string Project, string Title, string Summary, string State, string? TaskUrl, string? PrUrl, long StartedAt, long? EndedAt, long UpdatedAt, long Revision);
