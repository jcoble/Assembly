namespace Assembly.Application.Features.Chat.Rows;

/// <summary>A prompt that attached images: its seq, and its trailing "Attached image:" lines.</summary>
public sealed record PromptAttachments(long Seq, string Lines);
