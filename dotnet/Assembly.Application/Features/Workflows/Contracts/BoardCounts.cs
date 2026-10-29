namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board Counts contract.</summary>
public sealed record BoardCounts(long Total, long Running, long NeedsYou, long Terminal);
