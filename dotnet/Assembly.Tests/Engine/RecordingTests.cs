using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Recording;

namespace Assembly.Tests.Engine;

public sealed class RecordingTests
{
    private static readonly EngineActor Reporter = new(ActorKind.System, "adapter");
    private static readonly Attribution A = new("s", null, null, null, 0);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Turn_id_is_idempotent_and_sql_totals_are_exact()
    {
        using var f = new EngineFixture(); f.SeedSession("s");
        var cmd = new TurnUsage(A, "t", 10, 4, 3, 25, 100);
        var first = f.Apply(cmd, "u", actor: Reporter);
        Assert.True(first.Applied);
        Assert.Equal(first, f.Apply(cmd, "u2", actor: Reporter));
        var sum = new UsageQueries(f.Db).Sum("session", "s", default);
        Assert.Equal(1, f.Count("engine_usage")); Assert.Equal(10, sum.InputTokens);
        Assert.Equal(4, sum.OutputTokens); Assert.Equal(3, sum.CachedTokens);
        Assert.Equal(25, sum.CostNanodollars); Assert.Null(sum.ToolDurationMs);
        Assert.Equal(100, sum.TurnDurationMs); Assert.Equal(0, sum.ToolCalls);
    }

    [Fact]
    public void Preview_never_persists_secret_or_splits_unicode()
    {
        var p = Preview.Create(Json(new { token = "sk-secret", output = new string('界', 10000) }));
        Assert.DoesNotContain("sk-secret", p); Assert.DoesNotContain("token", p);
        Assert.True(Encoding.UTF8.GetByteCount(p) <= 4096); Assert.DoesNotContain("\uFFFD", p);
        Assert.Contains("30000", p);
        Assert.True(Encoding.UTF8.GetByteCount(Preview.Create(Json(Enumerable.Range(0, 10000).ToArray()))) <= 4096);
    }

    [Fact]
    public void Boundary_start_end_survives_restart()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedRun(); f.SeedStep(sessionId: "s");
        var a = new Attribution("s", "r", "step", 0, 0);
        var start = new ToolStarted(a, "provider", "call", "read", Json(new { token = "sk-secret" }), 987);
        var first = f.Apply(start, "tool", actor: Reporter);
        Assert.True(first.Applied);
        f.Reopen(); f.Time.Advance(TimeSpan.FromMilliseconds(50));
        f.Db.Writer.Write("test.advance-attribution", s =>
        {
            s.Execute("UPDATE engine_session SET epoch=1 WHERE id='s'");
            return s.Execute("UPDATE engine_step SET attempt=1 WHERE id='step'");
        });
        Assert.Equal(first, f.Apply(start, "another", actor: Reporter));
        var end = new ToolEnded(a, "succeeded", Json("secret-output"), 654);
        var result = f.Apply(end, "tool", actor: Reporter);
        Assert.True(result.Applied);
        Assert.Equal(result, f.Apply(end, "tool", actor: Reporter));
        Assert.Equal(1, f.Count("engine_tool_call"));
        Assert.Equal(987, f.Number("SELECT input_bytes FROM engine_tool_call"));
        Assert.Equal(654, f.Number("SELECT output_bytes FROM engine_tool_call"));
        Assert.Equal(1000, f.Number("SELECT started_ms FROM engine_tool_call"));
        Assert.Equal(1050, f.Number("SELECT ended_ms FROM engine_tool_call"));
        Assert.Equal(0, f.Number("SELECT epoch FROM engine_tool_call"));
        Assert.Equal(0, f.Number("SELECT attempt FROM engine_tool_call"));
        Assert.Equal("r", f.Text("SELECT run_id FROM engine_tool_call"));
        Assert.Equal("step", f.Text("SELECT step_id FROM engine_tool_call"));
        Assert.Equal(50, new UsageQueries(f.Db).Sum("step", "step", default).ToolDurationMs);
        Assert.DoesNotContain("sk-secret", f.Text("SELECT input_preview FROM engine_tool_call")!);
        Assert.DoesNotContain("secret-output", f.Text("SELECT output_preview FROM engine_tool_call")!);
        Assert.All(f.Events(), e => Assert.Equal("s", e.Data.SessionId));
        Assert.DoesNotContain("secret", string.Join("", f.Db.Query("SELECT result_json FROM engine_command_receipt", r => r.Text(0), default)));
    }

    [Fact]
    public void Mismatched_attribution_is_refused()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedRun(); f.SeedStep(sessionId: "s");
        var a = new Attribution("s", "r", "step", 0, 0);
        f.Apply(new ToolStarted(a, "provider", "call", "read", Json(0), 1), "tool", actor: Reporter);
        Attribution[] mismatches = [a with { SessionId = "other" }, a with { RunId = null },
            a with { StepId = null }, a with { Attempt = 1 }, a with { Epoch = 1 }];
        foreach (var mismatch in mismatches)
            Assert.Equal("attribution_mismatch", f.Apply(new ToolEnded(mismatch, "ok", Json(0), 1), "tool", actor: Reporter).Error);
        Assert.Null(f.Text("SELECT outcome FROM engine_tool_call"));
        Assert.Equal(1, f.Count("engine_event"));
    }

    [Fact]
    public void Run_under_another_session_is_refused()
    {
        using var f = new EngineFixture();
        f.SeedSession("a"); f.SeedSession("b"); f.SeedRun(ownerSessionId: "a");
        var attribution = new Attribution("b", "r", null, null, 0);
        var start = new ToolStarted(attribution, "provider", "call", "read", Json(0), 1);
        var usage = new TurnUsage(attribution, "turn", 10, 4, 3, 25, 100);
        var eventsBefore = f.Count("engine_event");

        var refusedStart = f.Apply(start, "tool", actor: Reporter);
        Assert.False(refusedStart.Applied);
        Assert.Equal("attribution_mismatch", refusedStart.Error);
        Assert.Equal(0, f.Count("engine_tool_call"));
        Assert.Equal(eventsBefore, f.Count("engine_event"));

        var refusedUsage = f.Apply(usage, "usage", actor: Reporter);
        Assert.False(refusedUsage.Applied);
        Assert.Equal("attribution_mismatch", refusedUsage.Error);
        Assert.Equal(0, f.Count("engine_usage"));
        Assert.Equal(eventsBefore, f.Count("engine_event"));

        var owner = attribution with { SessionId = "a" };
        Assert.True(f.Apply(start with { Attribution = owner }, "tool", actor: Reporter).Applied);
        Assert.True(f.Apply(usage with { Attribution = owner }, "usage", actor: Reporter).Applied);
        Assert.Equal(1, f.Count("engine_tool_call"));
        Assert.Equal(1, f.Count("engine_usage"));
        Assert.Equal(eventsBefore + 2, f.Count("engine_event"));
        Assert.All(f.Events(), e => Assert.Equal("a", e.Data.SessionId));
    }

    [Theory]
    [InlineData("ended", "running", "running")]
    [InlineData("idle", "succeeded", "running")]
    [InlineData("idle", "running", "succeeded")]
    public void Late_valid_fact_keeps_terminal_outcome(string session, string run, string step)
    {
        using var f = new EngineFixture(); f.SeedSession(state: session); f.SeedRun(state: run);
        f.SeedStep(state: step, sessionId: "s");
        var a = new Attribution("s", "r", "step", 0, 0);
        Assert.True(f.Apply(new ToolStarted(a, "mcp", "c", "read", Json(0), 1), "tool", actor: Reporter).Late);
        Assert.True(f.Apply(new ToolEnded(a, "ok", Json(0), 2), "tool", actor: Reporter).Late);
        Assert.True(f.Apply(new TurnUsage(a, "t", 1, 2, 1, 3, 4), "u", actor: Reporter).Late);
        Assert.Equal(1, f.Number("SELECT late FROM engine_tool_call"));
        Assert.Equal(1, f.Number("SELECT late FROM engine_usage"));
        Assert.Equal(run, f.State("engine_run", "r"));
        Assert.Equal(step, f.State("engine_step", "step"));
        Assert.All(f.Events(), e => { Assert.True(e.Late); Assert.EndsWith(".late", e.Kind); });
        Assert.Equal(3, new UsageQueries(f.Db).Sum("run", "r", default).CostNanodollars);
    }

    [Fact]
    public void Namespaces_conflicts_unknowns_and_separate_sums()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedSession("other");
        var start = new ToolStarted(A, "provider", "same", "read", Json(0), 3);
        Assert.True(f.Apply(start, "p", actor: Reporter).Applied);
        Assert.True(f.Apply(start with { Boundary = "mcp" }, "m", actor: Reporter).Applied);
        Assert.Equal("bad_args", f.Apply(start with { Tool = "other" }, "p2", actor: Reporter).Error);
        Assert.Equal("bad_args", f.Apply(start with { BoundaryId = "different" }, "p", actor: Reporter).Error);
        Assert.Equal("bad_args", f.Apply(new ToolEnded(A, "ok", Json(0), 1), "missing", actor: Reporter).Error);
        foreach (var id in new[] { "p", "m" })
            Assert.True(f.Apply(new ToolEnded(A, "ok", Json(0), 1), id, actor: Reporter).Applied);
        var usage = new TurnUsage(A, "same", 10, 4, 3, 25, 100);
        Assert.True(f.Apply(usage, "u", actor: Reporter).Applied);
        Assert.True(f.Apply(usage with { ProviderTurnId = "second" }, "u2", actor: Reporter).Applied);
        Assert.True(f.Apply(usage with { Attribution = A with { SessionId = "other" } }, "u3", actor: Reporter).Applied);
        Assert.Equal("bad_args", f.Apply(usage with { InputTokens = 11 }, "conflict", actor: Reporter).Error);
        Assert.True(f.Apply(new TurnUsage(A, "unknown", null, null, null, null, null), "unknown", actor: Reporter).Applied);
        var sum = new UsageQueries(f.Db).Sum("session", "s", default);
        Assert.Equal(20, sum.InputTokens); Assert.Equal(8, sum.OutputTokens); Assert.Equal(6, sum.CachedTokens);
        Assert.Equal(50, sum.CostNanodollars); Assert.Equal(200, sum.TurnDurationMs);
        Assert.Equal(2, sum.ToolCalls); Assert.Equal(0, sum.ToolDurationMs);
        var empty = new UsageQueries(f.Db).Sum("session", "absent", default);
        Assert.Null(empty.InputTokens); Assert.Null(empty.CostNanodollars); Assert.Null(empty.TurnDurationMs);
        Assert.Null(empty.ToolDurationMs); Assert.Equal(0, empty.ToolCalls);
        Assert.Throws<ArgumentException>(() => new UsageQueries(f.Db).Sum("anything", "s", default));
    }

    [Theory]
    [InlineData(ActorKind.Window)]
    [InlineData(ActorKind.Session)]
    public void Only_system_reporters_record(ActorKind actor)
    {
        using var f = new EngineFixture(); f.SeedSession();
        var who = new EngineActor(actor, "s");
        Assert.Equal("forbidden", f.Apply(new ToolStarted(A, "p", "c", "t", Json(0), 1), "c", actor: who).Error);
        Assert.Equal("forbidden", f.Apply(new ToolEnded(A, "ok", Json(0), 1), "c", actor: who).Error);
        Assert.Equal("forbidden", f.Apply(new TurnUsage(A, "t", 1, 1, 0, 1, 1), "u", actor: who).Error);
        Assert.Equal(0, f.Count("engine_event"));
    }

    [Fact]
    public void Negative_metrics_and_cached_over_input_are_refused_and_overflow_throws()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var u = new TurnUsage(A, "t", 10, 4, 3, 25, 100);
        TurnUsage[] invalid = [u with { InputTokens = -1 }, u with { OutputTokens = -1 },
            u with { CachedTokens = -1 }, u with { CostNanodollars = -1 }, u with { DurationMs = -1 },
            u with { CachedTokens = 11 }];
        foreach (var cmd in invalid) Assert.Equal("bad_args", f.Apply(cmd, "u", actor: Reporter).Error);
        var start = new ToolStarted(A, "p", "c", "t", Json(0), -1);
        Assert.Equal("bad_args", f.Apply(start, "c", actor: Reporter).Error);
        Assert.True(f.Apply(start with { InputBytes = 99 }, "c", actor: Reporter).Applied);
        Assert.Equal("bad_args", f.Apply(new ToolEnded(A, "ok", Json(0), -1), "c", actor: Reporter).Error);
        Assert.True(f.Apply(u with { CostNanodollars = long.MaxValue }, "u", actor: Reporter).Applied);
        Assert.True(f.Apply(u with { ProviderTurnId = "two" }, "u2", actor: Reporter).Applied);
        var overflow = Assert.Throws<SqliteException>(() => new UsageQueries(f.Db).Sum("session", "s", default));
        Assert.Contains("integer overflow", overflow.Message);
    }

    [Fact]
    public void Boundary_receipts_survive_event_removal_and_large_arguments_stay_transient()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var start = new ToolStarted(A, "provider", "c", "read", Json(new string('界', 20000)), 5);
        var result = f.Apply(start, "c", actor: Reporter);
        Assert.True(result.Applied);
        var usage = new TurnUsage(A, "t", null, null, null, null, null);
        var turn = f.Apply(usage, "u", actor: Reporter);
        f.Db.Writer.Write("test.compact-events", s => s.Execute("DELETE FROM engine_event"));
        f.Reopen();
        Assert.Equal(result, f.Apply(start, "different", actor: Reporter));
        Assert.Equal(turn, f.Apply(usage, "different", actor: Reporter));
        Assert.Equal(0, f.Count("engine_event"));
    }

    [Fact]
    public void Conflicting_scalar_values_are_refused_without_persisting_them()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var start = new ToolStarted(A, "provider", "c", "read", Json("secret1"), 7);
        Assert.True(f.Apply(start, "c", actor: Reporter).Applied);
        Assert.Equal("bad_args", f.Apply(start with { Input = Json("secret2") }, "other", actor: Reporter).Error);
        var end = new ToolEnded(A, "ok", Json("secret3"), 7);
        Assert.True(f.Apply(end, "c", actor: Reporter).Applied);
        Assert.Equal("bad_args", f.Apply(end with { Output = Json("secret4") }, "c", actor: Reporter).Error);
        Assert.DoesNotContain("secret", f.Text("SELECT input_preview || output_preview FROM engine_tool_call")!);
    }
}
