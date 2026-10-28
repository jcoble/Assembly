namespace Assembly.Application.Features.Chat.Rows;

/// <summary>A file a session's tool calls edited, deleted or moved: their lines added and removed, and the newest one's seq.</summary>
public sealed record FileTouched(string Path, long Added, long Removed, long LastSeq);
