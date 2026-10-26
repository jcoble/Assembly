using Assembly.Domain.Events;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>A turn in progress, and the tallies its stats row needs.</summary>
internal sealed class OpenTurn(string runId, long startedAt, decimal? sessionCostAtStart)
{
    private decimal? _costAtStart = sessionCostAtStart;
    private bool _costUnknown;

    public string RunId { get; } = runId;

    public long? FirstTextAt { get; set; }

    public int ToolCalls { get; set; }

    public string? Model { get; set; }

    /// <summary>
    /// The session opened during this turn. A new session's cost counts from zero; a resumed one's from the total
    /// saved at its last clean close. Without one, its starting total is unknown, so this turn has no cost.
    /// </summary>
    public void AgentStarted(bool resumed, decimal? savedCost)
    {
        _costAtStart = resumed ? savedCost : null;
        _costUnknown = resumed && savedCost is null;
    }

    /// <param name="sessionCost">The session's cost so far, as the agent last reported it.</param>
    public StatsPayload Stats(string status, long endedAt, TokenUsage? usage, decimal? sessionCost)
    {
        var (input, read, write) = (usage?.InputTokens ?? 0, usage?.CachedReadTokens ?? 0, usage?.CachedWriteTokens ?? 0);
        var allInput = input + read + write;
        return new StatsPayload(
            RunId,
            status,
            startedAt,
            DurationMs: endedAt - startedAt,
            FirstTokenMs: FirstTextAt - startedAt,
            Model,
            usage?.InputTokens,
            usage?.OutputTokens,
            CacheHitPct: usage is null || allInput == 0 ? null : Math.Round(100.0 * read / allInput, 1),
            // A failed or lost turn's total isn't settled, so its cost is unknown, not 0.
            CostUsd: _costUnknown || status == "failed" ? null : sessionCost - (_costAtStart ?? 0),
            ToolCalls);
    }
}
