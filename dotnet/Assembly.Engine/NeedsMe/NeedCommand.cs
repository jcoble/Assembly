using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;
using Assembly.Application.Common.Engine;

namespace Assembly.Engine.NeedsMe;

public abstract record NeedCommand : EngineCommand;
public sealed record OpenNeed(string SessionId, string? RunId, string? StepId,
    [property: JsonPropertyName("needKind")] string Kind, string Text, IReadOnlyList<string> Options, string? ProviderRequestId,
    string? ChatRowId, long? DeadlineMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? ProviderQuestion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NeedPresentation? Presentation = null) : NeedCommand;
public sealed record EscalateNeed() : NeedCommand;
public sealed record ResolveNeed(string State, JsonElement? Answer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null) : NeedCommand;

public sealed record NeedAnswer(string NeedId, string ProviderRequestId, JsonElement Answer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProviderAnswerBinding? Binding = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProviderTerminalBinding? Cancellation = null);
