using System.Text.Json.Serialization;

namespace Assembly.Domain.Events;

/// <summary>
/// The agent took a message into turn <c>RunId</c>, which may have ended by the time it said so. <c>ClientId</c> is
/// the sender's id for it.
/// </summary>
public sealed record TurnSteered(long At, string RunId, string Text, string? ClientId = null, string? SenderLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromSessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserItemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AttachmentIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromMachine = null) : AgentEvent(At);
