namespace Assembly.Application.Common.Acp.Messages;

/// <summary>
/// The insertion acknowledgement. Foreground completion arrives through state updates.
/// </summary>
public sealed record PromptResponse(string MessageId);
