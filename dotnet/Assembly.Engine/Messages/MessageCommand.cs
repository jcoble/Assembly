using Assembly.Engine.Contracts;

namespace Assembly.Engine.Messages;

public sealed record DeliverMessage(string MessageId, string ToSessionId, string Text,
    string SenderLabel, string How, string ClientId, long RecipientVersion);

public abstract record MessageCommand : EngineCommand;
public sealed record SendMessage(string? FromSessionId, string ToSessionId,
    string Text, string Mode) : MessageCommand;
public sealed record MessageOutcome(string State, string? ProviderTurnId,
    string? Reason) : MessageCommand;
