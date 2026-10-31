using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.Runs;

public abstract record RunCommand : EngineCommand;
public sealed record Dependency(string StepId, bool Optional);
public sealed record StepDefinition(string Id, string Name, bool Required,
    int MaxAttempts, bool RuleSkippable, IReadOnlyList<Dependency> Dependencies);
public sealed record StartRun(string OwnerSessionId, string TaskUrl,
    string? Worktree, string? Branch, long OwnerLossLimitMs,
    IReadOnlyList<StepDefinition> Steps) : RunCommand;
public sealed record CancelRun() : RunCommand;
