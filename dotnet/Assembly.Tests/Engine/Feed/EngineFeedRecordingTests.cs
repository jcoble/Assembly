using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine.Feed;

public sealed class EngineFeedRecordingTests
{
    [Fact]
    public async Task Old_tool_completion_uses_original_durable_attribution_and_cannot_rewrite_outcome()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession(); f.SeedRun(state: "running"); f.SeedStep(sessionId: "s", state: "running", attempt: 2);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var feed = new EngineFeed(runtime, TextWriter.Null, f.Time); var original = feed.Capture("s")!;
        var start = new ToolCallChanged(1000, "tool", true, new(Name: "read", Input: JsonSerializer.SerializeToElement(new { token = "secret" }), AcpSessionId: "acp"));
        f.Db.Writer.Write("test.start", s => { feed.ObserveBatch(s, original, "acp", [start, start]); return 0; });
        Assert.Equal(1, f.Count("engine_tool_call"));
        f.Db.Writer.Write("test.advance", s => s.Execute("UPDATE engine_session SET epoch=1 WHERE id='s'; UPDATE engine_step SET attempt=3 WHERE id='step'"));
        var current = feed.Capture("s")!;
        var end = start with { Started = false, Fields = start.Fields with { Status = "completed", Output = "private output" } };
        f.Db.Writer.Write("test.end", s => { feed.ObserveBatch(s, current, "acp", [end]); return 0; });
        f.Db.Writer.Write("test.repeat", s => { feed.ObserveBatch(s, current, "acp", [end with { Fields = end.Fields with { Status = "failed" } }]); return 0; });
        Assert.Equal(0, f.Number("SELECT epoch FROM engine_tool_call"));
        Assert.Equal(2, f.Number("SELECT attempt FROM engine_tool_call"));
        Assert.Equal(1, f.Number("SELECT late FROM engine_tool_call"));
        Assert.Equal("completed", f.Text("SELECT outcome FROM engine_tool_call"));
        Assert.DoesNotContain("secret", f.Text("SELECT input_preview FROM engine_tool_call")!);
        Assert.DoesNotContain("private", f.Text("SELECT output_preview FROM engine_tool_call")!);
    }

    [Fact]
    public async Task First_seen_terminal_patch_without_original_name_does_not_invent_a_current_turn_start()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var log = new StringWriter(); var feed = new EngineFeed(runtime, log, f.Time); var observed = feed.Capture("s")!;
        var notification = JsonSerializer.Deserialize("""{"sessionId":"acp","update":{"sessionUpdate":"tool_call_update","toolCallId":"old-unobserved","status":"completed"}}""",
            Assembly.Application.Common.Acp.AcpJsonContext.Default.SessionNotification)!;
        var events = new Assembly.Application.Common.Acp.V2.UpdateReader().Read(notification, 1000);
        Assert.True(Assert.IsType<ToolCallChanged>(Assert.Single(events)).Started); // first seen, not a protocol start
        f.Db.Writer.Write("test.orphan", s => { feed.ObserveBatch(s, observed, "acp", events); return 0; });
        Assert.Equal(0, f.Count("engine_tool_call"));
        Assert.Equal(0, f.Count("engine_command_receipt"));
        Assert.Contains("tool_start_provenance_missing", log.ToString());
    }

    [Theory]
    [InlineData(8, 80081, 12002, 92091)]
    [InlineData(10180, 123776, 0, 133956)]
    public async Task Final_usage_binds_actual_provider_id_full_cache_counts_and_replays_after_reopen(long input, long read, long write, long total)
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var feed = new EngineFeed(runtime, TextWriter.Null, f.Time); var original = feed.Capture("s")!;
        var final = new SessionTurnFinalization("actual-provider-message", 1000, 1100, "completed", new(input, 3, read, write), 0.0000000015m);
        Assert.True(f.Db.Writer.Write("test.complete", s => feed.Complete(s, original, "acp", final)));
        Assert.Equal(total, f.Number("SELECT input_tokens FROM engine_usage"));
        Assert.Equal(read, f.Number("SELECT cached_tokens FROM engine_usage"));
        Assert.Equal(2, f.Number("SELECT cost_nanodollars FROM engine_usage"));
        var receiptCount = f.Count("engine_command_receipt");
        await runtime.DisposeAsync(); f.Reopen();
        await using var reopened = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var resumed = new EngineFeed(reopened, TextWriter.Null, f.Time);
        Assert.True(f.Db.Writer.Write("test.replay", s => resumed.Complete(s, original, "acp", final)));
        Assert.Equal(1, f.Count("engine_usage"));
        Assert.False(f.Db.Writer.Write("test.conflict", s => resumed.Complete(s, original, "acp", final with { CostUsd = 1 })));
        Assert.Equal(2, f.Number("SELECT cost_nanodollars FROM engine_usage"));
        Assert.Equal("actual-provider-message", f.Text("SELECT provider_turn_id FROM engine_usage"));
        Assert.True(f.Count("engine_command_receipt") >= receiptCount);
    }

    [Fact]
    public async Task Unknown_partial_negative_and_overflow_do_not_reserve_final_usage_boundary()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var log = new StringWriter(); var feed = new EngineFeed(runtime, log, f.Time); var observed = feed.Capture("s")!;
        f.Db.Writer.Write("test.partial", s => { feed.ObserveBatch(s, observed, "acp", [new UsageReported(1000, 2, null)]); return 0; });
        Assert.Equal(0, f.Count("engine_usage"));
        var final = new SessionTurnFinalization(null, 100, 50, "completed", null, null);
        Assert.False(f.Db.Writer.Write("test.unknown", s => feed.Complete(s, observed, "acp", final)));
        foreach (var invalid in new[] { final with { ProviderMessageId = "m", CostUsd = -1 }, final with { ProviderMessageId = "m", CostUsd = decimal.MaxValue },
            final with { ProviderMessageId = "m", Usage = new(long.MaxValue, 0, 1, 0) } })
            Assert.False(f.Db.Writer.Write("test.invalid", s => feed.Complete(s, observed, "acp", invalid)));
        Assert.Equal(0, f.Count("engine_command_receipt"));
        Assert.True(f.Db.Writer.Write("test.known", s => feed.Complete(s, observed, "acp", final with { ProviderMessageId = "m", Usage = new(0, 0, null, 0) })));
        Assert.Null(f.Text("SELECT input_tokens FROM engine_usage"));
        Assert.Null(f.Text("SELECT cost_nanodollars FROM engine_usage"));
        Assert.Equal(0, f.Number("SELECT duration_ms FROM engine_usage"));
        Assert.Contains("invalid_usage_metric", log.ToString());
    }

    [Fact]
    public async Task Actual_answers_settle_only_the_matching_need_without_an_answer_effect()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession(state: "running");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        var feed = new EngineFeed(runtime, TextWriter.Null, f.Time); var observed = feed.Capture("s")!;
        var request = new JsonRpcRequestContext("connection", JsonSerializer.SerializeToElement("7"));
        var card = new AskPayload("permission", "open", "Run?", null, null, [new("yes", "Yes", "allow_once")], null, null);
        f.Db.Writer.Write("test.ask", s => { feed.Asked(s, observed, "ask", card, request); return 0; });
        var answer = JsonSerializer.SerializeToElement(new { optionId = "yes" });
        f.Db.Writer.Write("test.answer", s => { feed.Settled(s, observed, "ask", "answered", answer, "manual_answered"); return 0; });
        f.Db.Writer.Write("test.policy", s => { feed.PolicyAnswered(s, observed, request, "session/request_permission", JsonSerializer.SerializeToElement(new { outcome = "selected", optionId = "yes" })); return 0; });
        Assert.Equal("answered", f.State("engine_needs_me", "ask"));
        Assert.Equal("connection:\"7\"", f.Text("SELECT provider_request_id FROM engine_needs_me"));
        Assert.Equal(answer.GetRawText(), f.Text("SELECT answer_json FROM engine_needs_me"));
        Assert.Equal(0, f.Count("engine_outbox"));
        var count = f.Count("engine_needs_me");
        f.Db.Writer.Write("test.auto-only", s => { feed.PolicyAnswered(s, observed, request with { Id = JsonSerializer.SerializeToElement(7) }, "session/request_permission", answer); return 0; });
        Assert.Equal(count, f.Count("engine_needs_me"));
    }
}
