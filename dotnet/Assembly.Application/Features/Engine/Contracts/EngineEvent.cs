namespace Assembly.Application.Features.Engine.Contracts;

public sealed record EngineEvent(string Machine, long Seq, string Kind, string EntityKind,
    string EntityId, long Version, long AtMs, bool Late, EngineEventData Data);

public sealed record EngineEventData(string? SessionId, string? RunId, string? StepId,
    string? ParentId, string? State, long? Epoch);
