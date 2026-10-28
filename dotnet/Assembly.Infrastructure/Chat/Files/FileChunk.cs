namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// One part of a file's text from <c>Offset</c>: its <c>Content</c>, the whole file's <c>Size</c> in bytes, <c>Revision</c>
/// (the SHA-256 of the whole file) and <c>LineCount</c>, and where the next part starts, or null at the end. A
/// <c>Binary</c> file isn't text, so it has no content or lines.
/// </summary>
public sealed record FileChunk(string Content, long Size, string Revision, int LineCount, bool Binary, long? NextOffset);
