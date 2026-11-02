using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Recording;

public sealed record Attribution(string SessionId, string? RunId, string? StepId, int? Attempt, long Epoch);
public abstract record RecordingCommand : EngineCommand;
public sealed record ToolStarted(Attribution Attribution, string Boundary, string BoundaryId,
    string Tool, JsonElement Input, long InputBytes) : RecordingCommand;
public sealed record ToolEnded(Attribution Attribution, string Outcome, JsonElement Output,
    long OutputBytes) : RecordingCommand;
public sealed record TurnUsage(Attribution Attribution, string ProviderTurnId, long? InputTokens,
    long? OutputTokens, long? CachedTokens, long? CostNanodollars, long? DurationMs) : RecordingCommand;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record UsageTotals(long? InputTokens, long? OutputTokens, long? CachedTokens,
    long? CostNanodollars, long? ToolDurationMs, long? TurnDurationMs, long ToolCalls, long McpCalls = 0);
