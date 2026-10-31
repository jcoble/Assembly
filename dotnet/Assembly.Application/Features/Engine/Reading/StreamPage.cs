using System.Text.Json;
using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.Reading;

public sealed record StreamPage(long After, long Through, IReadOnlyList<EngineEvent> Events,
    bool HasMore, bool SnapshotRequired);
public sealed record SnapshotPage(long Through, IReadOnlyList<JsonElement> Items, bool HasMore);
