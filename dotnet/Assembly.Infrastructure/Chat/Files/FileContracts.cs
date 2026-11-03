namespace Assembly.Infrastructure.Chat.Files;

public sealed record FileFault(string Code, string Message);
public sealed record SaveResult(string State, string? Revision, FileFault? Error);
public sealed record BeginSave(string Session, string Path, string ExpectedRevision, long Size);
public sealed record BegunSave(string Upload);
public sealed record SaveChunk(string Upload, long Offset, byte[] Data);
public sealed record CommitSave(string Upload);
public sealed record DropSave(string Upload);
