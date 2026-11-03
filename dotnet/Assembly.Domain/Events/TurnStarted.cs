using System.Text.Json.Serialization;

namespace Assembly.Domain.Events;

/// <summary>We sent the agent a prompt. <c>RunId</c> names this turn; <c>ClientId</c> is the sender's id for the prompt.</summary>
public sealed record TurnStarted(long At, string RunId, string Text, string? ClientId = null, string? SenderLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromSessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserItemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AttachmentIds = null) : AgentEvent(At);
