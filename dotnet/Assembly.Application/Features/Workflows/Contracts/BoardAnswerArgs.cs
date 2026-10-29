namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board AnswerArgs contract.</summary>
public sealed record BoardAnswerArgs(string MachineId, string TaskKey, string QuestionKey, int Generation, string AnswerKey, BoardAnswerValue Answer);
