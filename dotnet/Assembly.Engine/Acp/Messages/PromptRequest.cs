namespace Assembly.Engine.Acp.Messages;

public sealed record PromptRequest(string SessionId, IReadOnlyList<TextBlock> Prompt);
