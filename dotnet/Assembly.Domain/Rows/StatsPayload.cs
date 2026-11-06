using System.Text.Json.Serialization;

namespace Assembly.Domain.Rows;

/// <summary>
/// The line under a turn. <c>Status</c> is <c>completed</c>, <c>failed</c> or <c>aborted</c>. Every field is
/// written, null or not, because the UI's type has them all. The git fields come later, from the backend running git.
/// <c>TotalTokens</c> is the turn's own total as the agent reported it, left out when there is none, so
/// <c>rows/tokens</c> can add a session's turns.
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
    int ToolCalls,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TotalTokens = null)
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
