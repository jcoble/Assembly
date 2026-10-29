namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board QuestionCursor contract.</summary>
public sealed record BoardQuestionCursor(long AskedAt, string QuestionKey);
