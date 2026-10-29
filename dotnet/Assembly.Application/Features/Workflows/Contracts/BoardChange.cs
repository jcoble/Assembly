namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board Change contract.</summary>
public sealed record BoardChange(string MachineId, string TaskKey, long Revision, long Seq);
