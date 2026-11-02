using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;

namespace Assembly.Engine.Relays;

public sealed record RelaySourceReadArgs(string MessageId, string RecipientMachine);
public sealed record RelaySendIdentity(string FromMachine, string MessageId, string SenderKind, SendMessage Command);
public sealed record RelaySourceFence(long MessageVersion, string State, long Attempt, bool InFlight,
    long SenderEpoch, string SenderHolder, long? SenderReservedEpoch, long RecipientStubEpoch, string RecipientHolder);
public sealed record RelaySourceCapture(RelaySendIdentity Identity, string RecipientMachine, string SenderLabel,
    RelaySourceFence Fence, string SourceSha256, string IdentitySha256);
public enum RelayPartKind { Body, SenderLabel }
public sealed record RelayPartManifest(long Utf8Bytes, string Sha256);
public sealed record RelaySourceDescriptor(string FromMachine, string RecipientMachine, string MessageId,
    string? FromSessionId, string SenderKind, string ToSessionId, string Mode, RelaySourceFence Fence,
    string IdentitySha256, string SourceSha256, string CommandSha256, long CommandUtf8Bytes,
    RelayPartManifest Body, RelayPartManifest SenderLabel);
public sealed record RelayDestinationEvidence(string IdentitySha256, string SourceSha256, bool Present,
    long? MessageVersion, string? State, string? ProviderTurnId, string? Reason,
    long RecipientEpoch, string RecipientHolder, long? RecipientReservedEpoch);
public sealed record RelaySendAck(string IdentitySha256, string FirstKey, CommandResult? First,
    string RetryKey, CommandResult? Retry, bool DestinationPresent, RelayDestinationEvidence? Destination = null,
    bool Unknown = false);
public sealed record RelaySendRequest(RelaySourceDescriptor Source, long PreparedRecipientEpoch, string? PredecessorRefusalKey = null);
public sealed record RelaySourceOpened(string Handle, RelaySourceDescriptor Source);
public sealed record RelaySourcePartArgs(string Handle, RelayPartKind Part, long Offset, int Limit = 1048576);
public sealed record RelaySourcePart(byte[] Data, long Offset, long Next, long Total);
public sealed record RelayHandleArgs(string Handle);
public sealed record RelaySendBeginReply(string Status, string? Transfer, RelaySendAck Ack);
public sealed record RelaySendPartArgs(string Transfer, RelayPartKind Part, long Offset, byte[] Data);
public sealed record RelaySendPartAccepted(long BodyNext, long SenderLabelNext);
public sealed record RelaySendCommitArgs(RelaySendRequest Request, string? Transfer = null);
public sealed record RelayOutcomeArgs(string RecipientMachine, RelaySourceDescriptor Source,
    string IdentitySha256, long ExpectedVersion, long Epoch, MessageOutcome Outcome,
    RelaySendAck DestinationAck);

internal sealed class RelayRefused(string error) : Exception(error)
{
    internal string Error { get; } = error;
}
