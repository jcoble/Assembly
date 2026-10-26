namespace Assembly.Application.Common.Acp.Messages;

public sealed record PromptRequest(string SessionId, IReadOnlyList<TextBlock> Prompt);
