namespace Assembly.Application.Features.Chat.Rows.Context;

/// <summary>
/// A file a session named (<c>rows/files</c>): lines added and removed by its calls that didn't fail, how many calls or
/// prompts named it, the newest one's seq, and an attached image's name.
/// </summary>
public sealed record FileTouched(string Path, long Added, long Removed, long Count, long LastSeq, string? Name = null);
