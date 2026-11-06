using System.Text.Json.Serialization;

namespace Assembly.Domain.Rows;

/// <summary>
/// A prompt: a person's, or a message from another session (<c>From</c>). <c>ClientId</c> is the id the sender gave
/// it, so a resend of the same message finds this row. <c>Named</c> lists the absolute or <c>~/</c> paths its text
/// mentions, and <c>Attached</c> the images its trailing "Attached image:" lines name.
/// </summary>
public sealed record UserPayload(string Text, string? From = null, string? ClientId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UserAcknowledgment? Acknowledgment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SenderLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthSendBlocker? AuthBlocker = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthRetryExecution? RetryExecution = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ItemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AttachmentIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromMachine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Named = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AttachedImage>? Attached = null);
