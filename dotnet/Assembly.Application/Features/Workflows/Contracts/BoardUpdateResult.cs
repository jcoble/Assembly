namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board UpdateResult contract.</summary>
public sealed record BoardUpdateResult(string MachineId, string TaskKey, long Revision, string? QuestionKey, int? Generation, BoardAnswerResult? Answer);
