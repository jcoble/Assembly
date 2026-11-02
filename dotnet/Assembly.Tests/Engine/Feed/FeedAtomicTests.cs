using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine.Feed;

public sealed class FeedAtomicTests
{
    [Fact]
    public async Task Empty_batch_runs_the_feed_without_a_chat_sequence_or_row()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        using var hub = new LiveHub(RowStore.Open(f.Db));
        var feed = new EngineFeed(runtime, TextWriter.Null);
        var observed = Assert.IsType<SessionObservation>(feed.Capture("s"));
        var saved = hub.Publish("s", new RowBatch(), scope => feed.ObserveBatch(scope, observed, "acp", [new TurnStarted(1000, "synthetic-ignored-by-host", "")]));
        Assert.Empty(saved);
        Assert.Equal("running", f.State("engine_session", "s"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM rows"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM sessions"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observer_failure_rolls_back_chat_and_Engine_and_retry_keeps_original_epoch(bool publicationFailure)
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        using var hub = new LiveHub(RowStore.Open(f.Db));
        var feed = new EngineFeed(runtime, TextWriter.Null);
        var observed = Assert.IsType<SessionObservation>(feed.Capture("s"));
        var batch = new RowConverter().Apply(new TurnStarted(1000, "chat-turn", "hello"));
        batch.Details.Add(new ToolDetail("call", Input: "{}", Output: "not committed"));
        batch.Terminals.Add(new TerminalChanged(1000, "terminal", Data: "bm90IGNvbW1pdHRlZA==", AcpSessionId: "acp"));
        Assert.Throws<InvalidOperationException>(() => hub.Publish("s", batch, scope =>
        {
            feed.ObserveBatch(scope, observed, "acp", [new TurnStarted(1000, "provider", "")]);
            if (publicationFailure)
                scope.Publish("test", null, default(System.Text.Json.JsonElement), Assembly.Engine.EngineJson.Default.JsonElement);
            throw new InvalidOperationException("injected before commit");
        }));
        Assert.Equal("idle", f.State("engine_session", "s"));
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_command_receipt"));
        Assert.Equal(0, hub.LastSeq("s"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM tool_details"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM acp_terminal_details"));
        f.Db.Writer.Write("test.new-epoch", s => s.Execute("UPDATE engine_session SET epoch=1 WHERE id='s'"));
        hub.Publish("s", batch, scope => feed.ObserveBatch(scope, observed, "acp", [new TurnStarted(1000, "provider", "")]));
        Assert.Equal("idle", f.State("engine_session", "s"));
        Assert.Equal(2, hub.LastSeq("s")); // valid chat survives a stale provider fact
    }
}
