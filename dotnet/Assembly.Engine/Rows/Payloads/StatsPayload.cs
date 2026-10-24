using System.Text.Json.Serialization;

namespace Assembly.Engine.Rows.Payloads;

/// <summary>
/// The line under a turn. <c>Status</c> is <c>completed</c>, <c>failed</c> or <c>aborted</c>. Every field is
/// written, null or not, because the UI's type has them all. The git fields come later, from the backend running git.
/// </summary>
public sealed record StatsPayload(
    string RunId,
    string Status,
    long? StartedAt,
    long? DurationMs,
    long? FirstTokenMs,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    double? CacheHitPct,
    decimal? CostUsd,
    int ToolCalls)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Branch { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Head { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PrevBranch { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PrevHead { get; init; }
}
