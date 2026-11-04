using Assembly.Engine.Contracts;
using System.Text.Json.Serialization;

namespace Assembly.Engine.Messages;

public sealed record DeliverMessage(string MessageId, string ToSessionId, string Text,
    string SenderLabel, string How, string ClientId, long RecipientVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Framed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AttachmentIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? HeldEpoch = null);

public abstract record MessageCommand : EngineCommand;
public sealed record SendMessage(string? FromSessionId, string ToSessionId,
    string Text, string Mode) : MessageCommand;
public sealed record SendOwnerPrompt(string ToSessionId, string Text, string ClientId,
    IReadOnlyList<string> AttachmentIds, long? TargetEpoch) : MessageCommand;
public sealed record MessageOutcome(string State, string? ProviderTurnId,
    string? Reason) : MessageCommand
{
    // Only the owning receipt projector supplies this; it is never a wire assertion.
    [JsonIgnore]
    internal Assembly.Application.Features.Chat.Rows.FinalizedChatReply? CompletedTurn { get; init; }
}
