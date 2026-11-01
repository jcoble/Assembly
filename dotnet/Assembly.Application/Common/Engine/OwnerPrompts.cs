namespace Assembly.Application.Common.Engine;

public sealed record OwnerPromptRequest(string Session, string Text, string ClientId,
    IReadOnlyList<string> AttachmentIds, long? Epoch);
public sealed record OwnerPromptAccepted(string SessionId, string MessageId, string ClientId,
    string State, long MessageVersion, long Seq, long Epoch, string? RunId);
public sealed record OwnerPromptManifest(string ClientId, string ContentSha256,
    string TextPreview, bool TextComplete, long TextUtf8Bytes, long AttachmentJsonUtf8Bytes, long AttachmentCount,
    long? TargetEpoch, long HeldEpoch, string State, long Version);
public sealed record OwnerPromptReadArgs(string Session, string? Machine = null,
    long? AfterCreatedMs = null, string? AfterClientId = null, int Limit = 50);
public sealed record OwnerPromptPage(string EngineMachine, IReadOnlyList<OwnerPromptManifest> Items,
    long? NextCreatedMs, string? NextClientId);
public sealed record OwnerPromptChunkArgs(string Session, string ClientId, string ContentSha256, string Part,
    long Offset, int Count = 1024 * 1024, string? Machine = null);
public sealed record OwnerPromptChunk(string ContentSha256, string Part, long Offset, long TotalBytes, byte[] Data, long? NextOffset);
