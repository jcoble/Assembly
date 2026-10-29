namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board UpdateArgs contract.</summary>
public sealed record BoardUpdateArgs(string TaskKey, string Title, string State, string Summary, string? TaskUrl, string? PrUrl, string[]? ChangedPaths, BoardQuestionAsk? Question, BoardAnswerReceived? AnswerReceived);
