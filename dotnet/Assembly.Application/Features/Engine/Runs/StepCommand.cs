using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.Runs;

public abstract record StepCommand : EngineCommand;
public sealed record StartStep(string SessionId) : StepCommand;
public sealed record SetStepState(string State, int Attempt,
    IReadOnlyList<string> ProofUrls, string? Reason, bool ByWorkflowRule) : StepCommand;
