using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.Messages;

public abstract record MessageCommand : EngineCommand;
public sealed record SendMessage(string? FromSessionId, string ToSessionId,
    string Text, string Mode) : MessageCommand;
public sealed record MessageOutcome(string State, string? ProviderTurnId,
    string? Reason) : MessageCommand;
