using System.Text.Json.Serialization;
using Assembly.Engine.Reading;

namespace Assembly.Engine.Messages.Reading;

public sealed record MessageKey(string OriginMachine, string Id);
public sealed record MessageSessionKey(string Machine, string Id);
public sealed record MessageStorage(string Machine, string Id);
public sealed record MessageScope(string? Machine, string? SessionMachine, string? SessionId,
    string? RunMachine, string? RunId, string? State, string Search, string Sort);
public sealed record MessageCut(string Machine, long Through, CursorAt? At, long? Generation = null);
public sealed record MessageOrder(long OriginCreatedMs, string OriginMachine, string Id);
public sealed record MessageCursor(string QueryHash, IReadOnlyList<MessageCut> Cuts, MessageOrder? After);
public sealed record MessageDisplay(MessageKey Key, MessageStorage Storage, long Version,
    string SenderMachine, string? FromSessionId, string SenderName, string RecipientMachine, string ToSessionId,
    string ReceiverName, string? RunMachine, string? RunId, long CreatedMs, string Kind, string State,
    string Delivery, string? Reason, string Preview, bool BodyAvailable);
public sealed record MessagePage(IReadOnlyList<MessageDisplay> Items, string? Next,
    IReadOnlyList<MessageCut> Cut, IReadOnlyList<string> MissingMachines, IReadOnlyList<MessageStorage> SourceUnknown)
{
    [JsonIgnore] public MessageCursor? Captured { get; init; }
}
public sealed record SeenCut(string Machine, long Through, CursorAt? At, long? Generation = null);
public sealed record MessageCount(long Count, IReadOnlyList<MessageCut> Cut,
    IReadOnlyList<string> MissingMachines, bool Partial)
{
    [JsonIgnore] public MessageCursor? Captured { get; init; }
}
public sealed record BodyArgs(string MessageId, string Machine);
public sealed record MatchArgs(IReadOnlyList<string> Ids, string Search);
public sealed record BodyMatch(IReadOnlyList<string> Ids);
/// <summary>The requested storage authority's actual bytes and facts; a missing key is explicitly unknown provenance.</summary>
public sealed record MessageBody(MessageKey? Key, MessageStorage Storage, long Version, string Text,
    string? SenderMachine, string? FromSessionId, string SenderName, string? RecipientMachine, string ToSessionId,
    string ReceiverName, string? RunMachine, string? RunId, long CreatedMs, string Kind, string State,
    string Delivery, string? Reason, long? MessageCreatedSeq);
