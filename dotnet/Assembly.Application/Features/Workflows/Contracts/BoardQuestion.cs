namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board Question contract.</summary>
public sealed record BoardQuestion(string MachineId, string TaskKey, string QuestionKey, int Generation, string Kind, string Prompt, string? Recommendation, long AskedAt, BoardAnswerValue? Answer, long? AnsweredAt, string? AnswerKey, string? DeliveryClientId, string? DeliveryTurnId, string DeliveryState, long? AcceptedRevision);
