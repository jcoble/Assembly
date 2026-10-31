using System.Text.Json;
using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.Recording;

public sealed record Attribution(string SessionId, string? RunId, string? StepId, int? Attempt, long Epoch);
public abstract record RecordingCommand : EngineCommand;
public sealed record ToolStarted(Attribution Attribution, string Boundary, string BoundaryId,
    string Tool, JsonElement Input, long InputBytes) : RecordingCommand;
public sealed record ToolEnded(Attribution Attribution, string Outcome, JsonElement Output,
    long OutputBytes) : RecordingCommand;
public sealed record TurnUsage(Attribution Attribution, string ProviderTurnId, long? InputTokens,
    long? OutputTokens, long? CachedTokens, long? CostNanodollars, long? DurationMs) : RecordingCommand;
public sealed record UsageTotals(long? InputTokens, long? OutputTokens, long? CachedTokens,
    long? CostNanodollars, long? ToolDurationMs, long? TurnDurationMs, long ToolCalls);
