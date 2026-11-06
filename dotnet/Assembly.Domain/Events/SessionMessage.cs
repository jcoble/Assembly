namespace Assembly.Domain.Events;

/// <summary>A provider acknowledgement of user prompt content; not a provider-native inter-session message.</summary>
public sealed record SessionMessage(long At, string? MessageId, string Text) : AgentEvent(At);
