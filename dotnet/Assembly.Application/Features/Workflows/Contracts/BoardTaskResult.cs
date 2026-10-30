namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board TaskResult contract.</summary>
public sealed record BoardTaskResult(BoardTask Task, BoardQuestion[] Questions, BoardEvent[] Feed, BoardMetrics Metrics, long? NextBeforeSeq, string? NextQuestionCursor, IReadOnlyList<BoardStep> Steps);
