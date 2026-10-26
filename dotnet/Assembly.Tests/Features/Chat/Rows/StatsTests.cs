using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Features.Chat.Rows;

public sealed class StatsTests
{
    [Fact]
    public void A_turn_costs_what_the_session_cost_grew_by_during_it()
    {
        // ACP's usage_update reports the session's cost so far, not the turn's.
        var turns = Converted.From(
        [
            new TurnStarted(0, "run-1", "one"), new UsageReported(10, 0.10m, null), new TurnEnded(20, "end_turn", null),
            new TurnStarted(30, "run-2", "two"), new UsageReported(40, 0.25m, null), new TurnEnded(50, "end_turn", null),
        ]);
        var costs = turns.OfKind(RowKind.Stats).Select(row => row.Payload.GetProperty("costUsd").GetDecimal());
        Assert.Equal([0.10m, 0.15m], costs);
    }

    [Fact]
    public void Each_run_keeps_the_latest_context_window_report_and_skips_repeats()
    {
        var turns = Converted.From(
        [
            new TurnStarted(0, "run-1", "one"), new UsageReported(10, null, null, 20_813, 1_000_000), new UsageReported(11, 0.1m, null, 20_813, 1_000_000),
            new UsageReported(12, null, null, 25_962, 1_000_000), new UsageReported(13, null, null), new TurnEnded(20, "end_turn", null),
            new TurnStarted(30, "run-2", "two"), new UsageReported(40, null, null, 25_962, 1_000_000), new TurnEnded(50, "end_turn", null),
        ]);
        var context = turns.OfKind(RowKind.Context)
            .Select(row => (row.ItemId, Converted.Get(row, "used").GetInt64(), Converted.Get(row, "size").GetInt64()));
        Assert.Equal([("context:run-1", 20_813L, 1_000_000L), ("context:run-1", 25_962L, 1_000_000L), ("context:run-2", 25_962L, 1_000_000L)], context);
    }

    [Theory]
    [InlineData("acp/tool-fails.jsonl", 25_962, 1_000_000)]
    [InlineData("acp/codex-background.jsonl", 30_109, 237_500)]
    public void The_context_window_is_read_from_both_agents_usage_reports(string fixture, long used, long size)
    {
        var last = Converted.Fixture(fixture).Last(RowKind.Context);
        Assert.Equal((used, size), (Converted.Get(last, "used").GetInt64(), Converted.Get(last, "size").GetInt64()));
    }

    [Fact]
    public void A_new_session_counts_its_cost_from_zero()
    {
        // Each adapter process sums the cost itself, so a new session's reports start again near zero.
        // The agent starts inside the turn, after the user row has gone out.
        var turns = Converted.From(
        [
            new TurnStarted(0, "run-1", "one"), new AgentStarted(5, Resumed: false), new UsageReported(10, 0.20m, null), new TurnEnded(20, "end_turn", null),
            new TurnStarted(30, "run-2", "two"), new AgentStarted(35, Resumed: false), new UsageReported(40, 0.05m, null), new TurnEnded(50, "end_turn", null),
            new TurnStarted(60, "run-3", "three"), new UsageReported(70, 0.12m, null), new TurnEnded(80, "end_turn", null),
        ]);
        Assert.Equal([0.20m, 0.05m, 0.07m], Costs(turns));
    }

    [Fact]
    public void A_resumed_agents_first_turn_has_no_cost()
    {
        // A resumed process may carry on from a total an earlier process saved when it exited cleanly,
        // or start from zero if that one was killed. Its first report can't tell the two apart.
        var turns = Converted.From(
        [
            new TurnStarted(0, "run-1", "one"), new AgentStarted(5, Resumed: false), new UsageReported(10, 0.559m, null), new TurnEnded(20, "end_turn", null),
            new TurnStarted(30, "run-2", "two"), new AgentStarted(35, Resumed: true), new UsageReported(40, 0.586m, null), new TurnEnded(50, "end_turn", null),
            new TurnStarted(60, "run-3", "three"), new UsageReported(70, 0.62m, null), new TurnEnded(80, "end_turn", null),
        ]);
        Assert.Equal([0.559m, null, 0.034m], Costs(turns));
    }

    [Fact]
    public void A_failed_or_lost_turn_has_no_cost()
    {
        // The session's total isn't settled when a turn fails, so a cost there would be a guess (often 0).
        var turns = Converted.From(
        [
            new TurnStarted(0, "run-1", "one"), new UsageReported(10, 0.10m, null), new TurnEnded(20, "end_turn", null),
            new TurnStarted(30, "run-2", "two"), new UsageReported(40, 0.12m, null), new TurnFailed(50, "boom", null),
            new TurnStarted(60, "run-3", "three"), new AgentLost(70),
        ]);
        Assert.Equal([0.10m, null, null], Costs(turns));
    }

    private static List<decimal?> Costs(Converted turns) =>
        turns.OfKind(RowKind.Stats)
            .Select(row => row.Payload.TryGetProperty("costUsd", out var cost) ? cost.GetDecimal() : (decimal?)null)
            .ToList();
}
