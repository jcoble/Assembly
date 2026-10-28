namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// The texts of a session's prompts that may have attached images, newest first, whose trailing "Attached image:" lines
/// name them; and the machine that runs the session, null for this one. A remote session's images stay on its machine.
/// </summary>
public sealed record SessionAttachments(string? Machine, IReadOnlyList<string> Prompts);
