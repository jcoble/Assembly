namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board QuestionAsk contract.</summary>
public sealed record BoardQuestionAsk(string QuestionKey, int ExpectedGeneration, string Kind, string Prompt, string? Recommendation);
