using Assembly.Application.Common.Database;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.TurnReplies;

public sealed class EngineFeedReplyTests
{
    [Theory]
    [InlineData(false, null, "")]
    [InlineData(false, "partial 世界\0", "partial 世界\ufffd")]
    [InlineData(true, "foreign partial", "foreign partial")]
    public async Task Actual_supplied_writer_recovery_inserts_once_and_preserves_display_only_locality(bool foreignParent, string? text, string expected)
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "checkouts");
        var parent = sessions.Create("claude", "https://github.com/o/r");
        var child = sessions.Helper(parent, "claude", "Captured 世界\0");
        var store = RowStore.Open(f.Db); using var hub = new LiveHub(store);
        var batch = new RowBatch();
        batch.Rows.Add(new("chat", null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 100));
        if (text is not null) batch.Rows.Add(new("chat", "a", "delta", JsonSerializer.SerializeToElement(new { text }), 101));
        store.Commit(child.SessionId, batch);
        var launcher = new FakeLauncher();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, sessions, launcher, "checkouts", f.Time));
        if (foreignParent) f.Db.Writer.Write("test.foreign", s => s.Execute("UPDATE engine_session SET machine='peer',holder='peer',state='foreign' WHERE id=$id", new SqlArg("$id", parent.SessionId)));
        var feed = new EngineFeed(runtime, TextWriter.Null, f.Time);
        Assert.Equal(1, RunRecovery.CloseUnfinished(store, hub, 1000, _ => true, feed.Recovered));
        Assert.Equal(expected, ReadBody()); Assert.Equal(1, f.Count("engine_message"));
        var assistant = store.RunRows(child.SessionId, "chat").LastOrDefault(r => r.Kind == "message");
        Assert.Equal(expected, assistant is null ? "" : PlainText.Of(assistant.Payload.GetProperty("blocks")));
        Assert.Equal("failed", store.RunRows(child.SessionId, "chat")[^1].Payload.GetProperty("state").GetString());
        Assert.Equal("backend_stopped", store.RunRows(child.SessionId, "chat").Single(r => r.Kind == "error").Payload.GetProperty("code").GetString());
        Assert.Equal(0, RunRecovery.CloseUnfinished(store, hub, 2000, _ => true, feed.Recovered));
        Assert.Equal(1, f.Count("engine_message")); Assert.Equal(0, launcher.Launches);
        string ReadBody() => f.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message", r => new System.Text.UTF8Encoding(false, true).GetString(r.Blob(0)), default)!;
    }

    [Fact]
    public async Task Owning_recovery_failure_rolls_back_partial_and_terminal_then_reopen_retries_once()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "checkouts"); var parent = sessions.Create("claude", "https://github.com/o/r");
        var child = sessions.Helper(parent, "claude", "Child"); var store = RowStore.Open(f.Db); using var hub = new LiveHub(store);
        var batch = new RowBatch(); batch.Rows.Add(new("chat", null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 100)); store.Commit(child.SessionId, batch);
        var launcher = new FakeLauncher();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, sessions, launcher, "checkouts", f.Time));
        var feed = new EngineFeed(runtime, TextWriter.Null, f.Time);
        f.Db.Writer.Write("test.fault", s => s.Execute("CREATE TRIGGER reply_fault BEFORE INSERT ON engine_message BEGIN SELECT RAISE(ABORT,'reply fault'); END"));
        Assert.ThrowsAny<Exception>(() => RunRecovery.CloseUnfinished(store, hub, 1000, _ => true, feed.Recovered));
        Assert.Single(store.UnfinishedRuns()); Assert.Equal(0, f.Count("engine_message")); Assert.DoesNotContain(store.RunRows(child.SessionId, "chat"), r => r.Kind == "error");
        f.Db.Writer.Write("test.remove-fault", s => s.Execute("DROP TRIGGER reply_fault"));
        Assert.Equal(1, RunRecovery.CloseUnfinished(store, hub, 1000, _ => true, feed.Recovered)); Assert.Equal(1, f.Count("engine_message"));
    }
    [Theory]
    [InlineData("UPDATE engine_session SET holder='retired' WHERE id=$id")]
    [InlineData("UPDATE engine_session SET reserved_epoch=1 WHERE id=$id")]
    [InlineData("UPDATE engine_session SET machine='peer',holder='peer',state='foreign' WHERE id=$id")]
    [InlineData("UPDATE session_info SET helper=0 WHERE session_id=$id")]
    public async Task Recovery_keeps_ordinary_chat_cleanup_but_ineligible_ownership_never_originates_reply(string mutation)
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        var sessions = SessionStore.Open(f.Db, f.Time, "checkouts"); var parent = sessions.Create("claude", "https://github.com/o/r");
        var child = sessions.Helper(parent, "claude", "Child"); var store = RowStore.Open(f.Db); using var hub = new LiveHub(store);
        var batch = new RowBatch(); batch.Rows.Add(new("chat", null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 100)); store.Commit(child.SessionId, batch);
        var launcher = new FakeLauncher();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, sessions, launcher, "checkouts", f.Time));
        f.Db.Writer.Write("test.eligibility", scope => scope.Execute(mutation, new SqlArg("$id", child.SessionId)));
        var feed = new EngineFeed(runtime, TextWriter.Null, f.Time);
        Assert.Equal(1, RunRecovery.CloseUnfinished(store, hub, 1000, _ => true, feed.Recovered));
        Assert.Equal("failed", store.RunRows(child.SessionId, "chat")[^1].Payload.GetProperty("state").GetString());
        Assert.Equal(0, f.Count("engine_message")); Assert.Equal(0, launcher.Launches);
    }

}
