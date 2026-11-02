using System.Text.Json;

namespace Assembly.Domain.Events;

/// <summary>
/// Observed provider-native inter-session message content and participants, resolved after each upsert or chunk.
/// This is provider provenance, separate from user prompt acknowledgements and Assembly message delivery.
/// Protocol metadata remains in the full wire transcript; chunks append content without changing participants.
/// </summary>
public sealed record ProviderSessionMessage(long At, string SessionId, string MessageId, string? SenderSessionId,
    string? RecipientSessionId, string? SenderMessageId, string? RecipientMessageId,
    IReadOnlyList<JsonElement> Content) : AgentEvent(At);
