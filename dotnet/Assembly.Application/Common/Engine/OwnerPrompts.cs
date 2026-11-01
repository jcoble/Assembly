namespace Assembly.Application.Common.Engine;

public sealed record OwnerPromptRequest(string Session, string Text, string ClientId,
    IReadOnlyList<string> AttachmentIds, long? Epoch);
public sealed record OwnerPromptAccepted(string SessionId, string MessageId, string ClientId,
    string State, long MessageVersion, long Seq, long Epoch, string? RunId);
public sealed record OwnerPromptReplay(string Id, string Machine, string ToSessionId, string ClientId, string Text,
    IReadOnlyList<string> AttachmentIds, long? TargetEpoch, long HeldEpoch, string State, long Version,
    string? ProviderTurnId, string? RunId);
public sealed record OwnerPromptReadArgs(string Session, string? Machine = null,
    long? AfterCreatedMs = null, string? AfterId = null, int Limit = 50);
public sealed record OwnerPromptPage(IReadOnlyList<OwnerPromptReplay> Items, long? NextCreatedMs, string? NextId);
