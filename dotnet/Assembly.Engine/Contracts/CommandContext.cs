using System.Text.Json.Serialization;

namespace Assembly.Engine.Contracts;

public sealed record CommandOrigin(string Channel, string? DeviceId, string? Surface);

public enum ActorKind { Window, Session, System }
public sealed record EngineActor(ActorKind Kind, string Id);

/// <summary>Identity and time supplied by the authenticated host, never by window arguments.</summary>
public sealed record CommandContext(Guid CommandId, string Machine, EngineActor Actor,
    string EntityId, long ExpectedVersion, long Epoch, long NowMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandOrigin? Origin = null);
