namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board ChangesResult contract.</summary>
public sealed record BoardChangesResult(BoardEvent[] Events, long NextSeq, long LatestSeq);
