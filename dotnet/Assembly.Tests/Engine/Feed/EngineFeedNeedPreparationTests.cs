using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Engine;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Feed;

public sealed class EngineFeedNeedPreparationTests
{
    [Fact]
    public async Task Prepared_full_card_is_frozen_before_writer_and_failed_row_transaction_can_retry_without_partial_need()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running");
        await using var runtime = EngineRuntime.Start(f.Db, new(), f.Time, f.Limits);
        var feed = new EngineFeed(runtime, TextWriter.Null, f.Time); var observed = feed.Capture("s")!;
        var rows = RowStore.Open(f.Db); var options = new List<AskOption> { new("", "label\0😀" + new string('x', 70000), "allow_once") };
        var card = new AskPayload("permission", "open", null, "", null, options, null, null);
        var before = f.Count("engine_command_receipt");
        Action<IWriteScope> prepared;
        using (var id = JsonDocument.Parse("\"original\""))
            prepared = feed.PrepareAsked(observed, "ask", card, new("connection", id.RootElement));
        Assert.Equal(before, f.Count("engine_command_receipt")); Assert.Equal(0, f.Count("engine_needs_me"));
        var batch = new RowConverter().Apply(new AskChanged(1000, "ask", card));
        options[0] = new("changed", "changed", "reject_once");
        f.Db.Writer.Write("test.fail", s => s.Execute("CREATE TRIGGER fail_need BEFORE INSERT ON engine_needs_me BEGIN SELECT RAISE(ABORT,'test rollback'); END"));
        Assert.Throws<SqliteException>(() => rows.Commit("s", batch, prepared));
        Assert.Equal(0, f.Count("rows")); Assert.Equal(0, f.Count("engine_needs_me")); Assert.Equal(before, f.Count("engine_command_receipt"));
        f.Db.Writer.Write("test.retry", s => s.Execute("DROP TRIGGER fail_need"));
        rows.Commit("s", batch, prepared);
        Assert.Equal("", f.Text("SELECT text FROM engine_needs_me"));
        Assert.Equal("[\"\"]", f.Text("SELECT options_json FROM engine_needs_me"));
        Assert.Equal("connection:\"original\"", f.Text("SELECT provider_request_id FROM engine_needs_me"));
        Assert.Equal(before + 1, f.Count("engine_command_receipt"));
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData("UPDATE engine_session SET epoch=1 WHERE id='s'")]
    [InlineData("UPDATE engine_session SET holder='remote' WHERE id='s'")]
    [InlineData("UPDATE engine_session SET reserved_epoch=1 WHERE id='s'")]
    public async Task Observation_changed_after_preparation_rolls_back_the_actual_ask_row(string change)
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running");
        await using var runtime = EngineRuntime.Start(f.Db, new(), f.Time, f.Limits);
        var feed = new EngineFeed(runtime, TextWriter.Null, f.Time); var observed = feed.Capture("s")!;
        var card = new AskPayload("question", "open", null, "Question", null, null, JsonSerializer.SerializeToElement(new { type = "object" }), null);
        var prepared = feed.PrepareAsked(observed, "ask", card, new("connection", JsonSerializer.SerializeToElement(7)));
        f.Db.Writer.Write("test.race", s => s.Execute(change));
        var error = Assert.Throws<CallFailed>(() => RowStore.Open(f.Db).Commit("s", new RowConverter().Apply(new AskChanged(1000, "ask", card)), prepared));
        Assert.Equal("provider_request_changed", error.Message);
        Assert.Equal(0, f.Count("rows")); Assert.Equal(0, f.Count("engine_needs_me")); Assert.Equal(0, f.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Full_policy_answer_is_frozen_and_settled_fact_suppresses_later_policy_callbacks()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running");
        await using var runtime = EngineRuntime.Start(f.Db, new(), f.Time, f.Limits);
        var feed = new EngineFeed(runtime, TextWriter.Null, f.Time); var observed = feed.Capture("s")!;
        var request = new JsonRpcRequestContext("connection", JsonSerializer.SerializeToElement(7));
        var card = new AskPayload("question", "open", null, "Form", null, null, JsonSerializer.SerializeToElement(new { type = "object" }), null);
        var rows = RowStore.Open(f.Db); var converter = new RowConverter();
        rows.Commit("s", converter.Apply(new AskChanged(1000, "ask", card)), feed.PrepareAsked(observed, "ask", card, request));
        var full = JsonSerializer.SerializeToElement(new { action = "accept", content = new { text = "\0λ😀" + new string('x', 80000) } });
        Action<IWriteScope> policy;
        using (var doc = JsonDocument.Parse(full.GetRawText())) policy = feed.PreparePolicyAnswered(observed, request, "elicitation/create", doc.RootElement);
        rows.Commit("s", converter.Apply(new AskChanged(1001, "ask", card with { State = "answered", Answer = full })), policy);
        var receipts = f.Count("engine_command_receipt");
        var stale = feed.PreparePolicyAnswered(observed, request, "elicitation/create", JsonSerializer.SerializeToElement(new { action = "cancel" }));
        f.Db.Writer.Write("test.stale", s => { stale(s); policy(s); return 0; });
        Assert.Equal(receipts, f.Count("engine_command_receipt"));
        Assert.Equal("answered", f.State("engine_needs_me", "ask"));
        using var stored = JsonDocument.Parse(f.Text("SELECT answer_json FROM engine_needs_me")!);
        Assert.True(JsonElement.DeepEquals(full, stored.RootElement)); Assert.Equal(0, f.Count("engine_outbox"));
    }
}
