using Assembly.Engine.Contracts;

namespace Assembly.Engine.Sessions;

public enum ForeignTitleSourceMode { NodeOnly, OwnedAncestry }
public enum ForeignTitleOperation { RegisterForeign, ForeignAncestry }
public enum ForeignTitlePartKind { NodeFacts, Titles }
public sealed record ForeignTitlePartManifest(long Utf8Bytes, string Sha256);
public sealed record ForeignTitleNode(int Index, string SessionId, string Machine, string Provider, string? ParentId,
    long TitleOffset, long TitleUtf8Bytes, string TitleSha256);
public sealed record ForeignTitleSourceOpenArgs(ForeignTitleSourceMode Mode, string AnchorSessionId, string TargetMachine);
public sealed record ForeignTitleSourceManifest(ForeignTitleSourceMode Mode, string SourceMachine, string AnchorSessionId,
    string TargetMachine, long NodeCount, ForeignTitlePartManifest NodeFacts, ForeignTitlePartManifest Titles);
public sealed record ForeignTitleSourceOpened(string Handle, ForeignTitleSourceManifest Manifest);
public sealed record ForeignTitleSourceChecked(string SourceMachine, string AnchorSessionId, string TargetMachine,
    long NodeCount, string OwnershipSha256);
public sealed record ForeignTitlePartArgs(string Handle, ForeignTitlePartKind Part, long Offset, int Limit = 1048576);
public sealed record ForeignTitlePart(byte[] Data, long Offset, long Next, long Total);
public sealed record ForeignAncestryProofQuery(string FromMachine, string ParentSessionId, long NodeCount, string StructureSha256);
public sealed record ForeignRegisterReceiptQuery(Guid CommandId, string FromMachine, string EntityId, long ExpectedVersion,
    long Epoch, ForeignRegisterCanonical Canonical);
public sealed record ForeignRegisterCanonical(string? SmallCanonical, string? CommandSha256, long? CommandUtf8Bytes, long? TitleUtf8Bytes);
public sealed record ForeignTitleRequest(ForeignTitleOperation Operation, string TargetMachine, string FromMachine, string EntityId,
    ForeignRegisterReceiptQuery? Registration, ForeignAncestryProofQuery? Ancestry, long NodeCount,
    ForeignTitlePartManifest NodeFacts, ForeignTitlePartManifest Titles);
public sealed record ForeignTitleAppendArgs(string Handle, ForeignTitlePartKind Part, long Offset, byte[] Data);
public sealed record ForeignTitleOffsets(long NodeFactsNext, long TitlesNext);
public sealed record ForeignTitleCommitArgs(ForeignTitleRequest Request, string? Handle = null);
public enum ForeignTitleReplyKind { PartsRequired, ExistingStructure, RecordedCanonical, Applied, Unknown }
public sealed record ForeignTitleReply(ForeignTitleReplyKind Kind, string? Handle, CommandResult? Result,
    string? VerifiedStructureSha256, string? ObservedStoredMetadataSha256);
