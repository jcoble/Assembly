namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board PageResult contract.</summary>
public sealed record BoardPageResult(BoardSummary[] Items, BoardCounts Counts, string? NextCursor, BoardWatermark[] Watermarks);
