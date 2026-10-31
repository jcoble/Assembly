using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.Handoffs;

public abstract record HandoffCommand : EngineCommand;
public sealed record RequestHandoff(string SessionId, string NewHolder) : HandoffCommand;
public sealed record HandoffOutcome(string State, long NextEpoch, string? Reason) : HandoffCommand;

public sealed record HandoffTransfer(string HandoffId, string SessionId, string NewHolder, long NextEpoch);
