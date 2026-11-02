namespace Assembly.Application.Common.Engine;

public sealed record SessionTitleReadArgs(string Session, string? Machine = null);
public sealed record SessionTitleStat(string EngineMachine, long Version, long Epoch, long Utf8Bytes, string Sha256);
public sealed record SessionTitleChunkArgs(string Session, long Version, long Epoch, long TotalBytes, long Offset,
    int Count = 1024 * 1024, string? Machine = null);
public sealed record SessionTitleChunk(long Version, long Epoch, long Offset, long TotalBytes, byte[] Data, long? NextOffset);
