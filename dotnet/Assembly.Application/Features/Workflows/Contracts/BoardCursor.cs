namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board Cursor contract.</summary>
public sealed record BoardCursor(int AttentionGroup, long UpdatedAt, string MachineId, string TaskKey);
