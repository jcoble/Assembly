namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board Summary contract.</summary>
public sealed record BoardSummary(BoardTask Task, int AttentionGroup, long QuestionCount, BoardMetrics Metrics);
