namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// The ends of a session's newest prompts that may have attached images, newest first, whose trailing "Attached image:"
/// lines name them; <c>More</c> when older ones were left out. <c>Machine</c> runs the session, null for this one: a
/// remote session's images stay on its machine.
/// </summary>
public sealed record SessionAttachments(string? Machine, IReadOnlyList<string> Prompts, bool More);
