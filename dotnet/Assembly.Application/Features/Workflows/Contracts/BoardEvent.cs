namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board Event contract.</summary>
public sealed record BoardEvent(string MachineId, long Seq, string TaskKey, string Kind, long OccurredAt, BoardTask Task, BoardQuestion[] Questions, string[]? ChangedPaths);
