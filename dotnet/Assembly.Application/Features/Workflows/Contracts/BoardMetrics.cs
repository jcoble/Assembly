namespace Assembly.Application.Features.Workflows.Contracts;

/// <summary>The Board Metrics contract.</summary>
public sealed record BoardMetrics(long? StepsDone, long? StepsFailed, long? StepsSkipped, long? Files, long? Tools);
