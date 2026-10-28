namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board AnswerReceived contract.</summary>
public sealed record BoardAnswerReceived(string QuestionKey, int Generation, string AnswerKey, BoardAnswerValue Answer);
