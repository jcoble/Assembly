using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.NeedsMe;

public abstract record NeedCommand : EngineCommand;
public sealed record OpenNeed(string SessionId, string? RunId, string? StepId,
    [property: JsonPropertyName("needKind")] string Kind, string Text, IReadOnlyList<string> Options, string? ProviderRequestId,
    string? ChatRowId, long? DeadlineMs) : NeedCommand;
public sealed record ResolveNeed(string State, JsonElement? Answer) : NeedCommand;

public sealed record NeedAnswer(string NeedId, string ProviderRequestId, JsonElement Answer);
