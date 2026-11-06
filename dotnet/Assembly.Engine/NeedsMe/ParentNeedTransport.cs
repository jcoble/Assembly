using Assembly.Engine.Relays;
using Assembly.Engine.Contracts;
using Assembly.Application.Common.Engine;

namespace Assembly.Engine.NeedsMe;

public sealed record ParentNeedInputArgs(string IntentId);
public sealed record ParentNeedInputOpened(string Handle, long Version, string State, long CreatedMs, RelayPartManifest Payload);
public sealed record ParentNeedInputPartArgs(string Handle, long Offset, int Limit = 1048576);
public enum ParentNeedUploadKind { Prepare, Execute }
public sealed record ParentNeedUploadArgs(ParentNeedUploadKind Kind, RelayPartManifest Payload,
    string? IntentId = null, long? IntentVersion = null);
public sealed record ParentNeedUploadOpened(string Handle);
public sealed record ParentNeedUploadPartArgs(string Handle, long Offset, byte[] Data);
public sealed record ParentNeedUploadOffset(long Next);
public sealed record ParentNeedCaptured(NeedAnswerTarget? Target, ProviderAnswerBinding? Binding, string? Error);
public sealed record ParentNeedDispatchArgs(string IntentId, long IntentVersion, string DescriptorH);
public sealed record ParentNeedOutcomeArgs(string IntentId, long IntentVersion, ParentNeedOutcome Outcome,
    NeedAnswerRequestDescriptor? RejectedPreparation = null);

public sealed record ParentNeedDispatchAck(CommandResult Result, bool Dispatched);
