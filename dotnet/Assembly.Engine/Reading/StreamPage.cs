using System.Text.Json;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Reading;

public sealed record StreamPage(long After, long Through, IReadOnlyList<EngineEvent> Events,
    bool HasMore, bool SnapshotRequired, IReadOnlyList<JsonElement>? Rows = null, CursorAt? At = null);
public sealed record SnapshotPage(long Through, IReadOnlyList<JsonElement> Items, bool HasMore, CursorAt? At = null);

public sealed record MachineInfo(string Machine, long Through);
public sealed record CursorAt(string Kind, string EntityId, long Version);
