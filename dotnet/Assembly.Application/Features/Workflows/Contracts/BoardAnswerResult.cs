namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board AnswerResult contract.</summary>
public sealed record BoardAnswerResult(string MachineId, string TaskKey, string QuestionKey, int Generation, string AnswerKey, long AcceptedRevision);
