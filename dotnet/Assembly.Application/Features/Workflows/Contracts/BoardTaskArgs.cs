namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board TaskArgs contract.</summary>
public sealed record BoardTaskArgs(string MachineId, string TaskKey, long? BeforeSeq, string? QuestionCursor);
