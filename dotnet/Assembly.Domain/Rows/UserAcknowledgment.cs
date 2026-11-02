namespace Assembly.Domain.Rows;

/// <summary>Positive provider evidence; injected steering has no provider message identity.</summary>
public sealed record UserAcknowledgment(string Kind, string? ProviderMessageId);
