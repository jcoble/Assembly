namespace Assembly.Application.Common.Engine;

public sealed record LegacySessionTitleRef(string SessionId, string PublisherEngineMachine,
    string? StoredMachine, string? ParentSessionId, string? TitleSource,
    long UpdatedAt, long Utf8Bytes, string Sha256);
public sealed record LegacySessionTitleStatArgs(LegacySessionTitleRef Reference);
public sealed record LegacySessionTitleChunkArgs(LegacySessionTitleRef Reference, long Offset, int Count);
public sealed record LegacySessionTitleStat(LegacySessionTitleRef Reference,
    long? OwningEngineVersion, long? OwningEngineEpoch);
public sealed record LegacySessionTitleChunk(long Offset, long TotalBytes, byte[] Data, long? NextOffset);
