using Assembly.Engine.Reading;

namespace Assembly.Engine.Messages.Reading;

public sealed record SenderOrderArgs(string Bucket, long Through, CursorAt? At, MessageOrder? After = null, int Limit = 200);
public sealed record SenderOrderFact(MessageKey Key, long CreatedMs, long? CreatedSeq, string Name);
public sealed record SenderOrderPage(IReadOnlyList<SenderOrderFact> Items, bool End);
public sealed record ReceiverOrderRef(string SessionId, long Version);
public sealed record ReceiverOrderArgs(IReadOnlyList<ReceiverOrderRef> Refs);
public sealed record ReceiverOrderFact(string SessionId, long Version, string Name);
public sealed record ReceiverOrderPage(IReadOnlyList<ReceiverOrderFact> Items);
public sealed record SenderPosition(string OriginMachine, MessageOrder? Consumed, long ScopeConsumed, bool End);
public sealed record MessageNamePosition(string Bucket, IReadOnlyList<SenderPosition> Sources, bool Completed = false);
public sealed record MessageDirectPage(MessagePage Page, string? FirstBlockedBucket);
public sealed record MessageReceiverAuthority(string Machine, string SessionId, long Version);
public sealed record MessageOrderInput(MessageKey Key, long CreatedMs, string? Name, int Input = 0);
public sealed record MessageScopeCoverage(string Machine, long Count);

public sealed record LocalSenderHead(MessageKey Key, long CreatedMs, long? CreatedSeq);
public sealed record LocalSenderPage(IReadOnlyList<LocalSenderHead> Items, bool End);
