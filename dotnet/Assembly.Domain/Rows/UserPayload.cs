using System.Text.Json.Serialization;

namespace Assembly.Domain.Rows;

/// <summary>
/// A prompt: a person's, or a message from another session (<c>From</c>). <c>ClientId</c> is the id the sender gave
/// it, so a resend of the same message finds this row.
/// </summary>
public sealed record UserPayload(string Text, string? From = null, string? ClientId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UserAcknowledgment? Acknowledgment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SenderLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthSendBlocker? AuthBlocker = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthRetryExecution? RetryExecution = null);
