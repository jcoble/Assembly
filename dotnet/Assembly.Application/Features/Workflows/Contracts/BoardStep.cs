namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>One ordered row in the current run snapshot.</summary>
public sealed record BoardStep(int Index, string Text, string? Tool, string? Detail, string State, string? Badge,
    string? Explanation, int? Progress, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, int? Attempt,
    int? MaxAttempts, string? QuestionKey, string? RequiredScope, bool? ScopeGranted);
