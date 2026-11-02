using System.Text.Json;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Features.Chat.Rows;

public sealed class TurnReplyRecoveryTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly AppDatabase _db;
    private readonly RowStore _store;
    private readonly LiveHub _hub;
    public TurnReplyRecoveryTests() { _db = TestDatabase.Open(_file.Path); _store = RowStore.Open(_db); _hub = new(_store); }
    private void Seed(string? partial)
    {
        var batch = new RowBatch();
        batch.Rows.Add(new("r", null, "turn", JsonSerializer.SerializeToElement(new { state = "started" }), 100));
        if (partial is not null)
            batch.Rows.Add(new("r", "assistant", "delta", JsonSerializer.SerializeToElement(new { text = partial }), 101));
        _store.Commit("child", batch);
    }
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("partial 世界\0", "partial 世界\ufffd")]
    public void Recovery_observes_actual_new_terminal_and_full_partial_in_same_writer(string? partial, string expected)
    {
        Seed(partial); var through = _store.LastSeq("child"); var calls = 0;
        Assert.Equal(1, RunRecovery.CloseUnfinished(_store, _hub, 222, _ => true, (scope, recovered) =>
        {
            calls++; Assert.Equal(new RecoveredChatTurn("child", "r", 1, through, 222), recovered);
            var reply = RowStore.TerminalReply(scope, "child", "r")!;
            Assert.Equal(expected, reply.Text);
            var assistant = scope.QueryFirst("SELECT payload FROM rows WHERE session_id='child' AND run_id='r' AND kind='message' ORDER BY seq DESC LIMIT 1", r => r.Text(0));
            Assert.Equal(expected, assistant is null ? "" : PlainText.Of(JsonDocument.Parse(assistant).RootElement.GetProperty("blocks")));
            Assert.Equal("failed", reply.TerminalState); Assert.True(reply.TerminalSeq > through);
            Assert.Equal(222, reply.EndedAt); Assert.Contains("backend stopped", reply.Error!);
            Assert.Equal("backend_stopped", scope.QueryFirst("SELECT json_extract(payload,'$.code') FROM rows WHERE kind='error'", r => r.Text(0)));
        }));
        Assert.Equal(1, calls); Assert.Empty(_store.UnfinishedRuns());
        Assert.Equal(0, RunRecovery.CloseUnfinished(_store, _hub, 333, _ => true, (_, _) => throw new Exception("Duplicate recovery")));
    }
    [Fact]
    public void Recovery_observer_failure_rolls_back_finalized_message_error_terminal_and_push()
    {
        Seed("partial"); using var subscription = _hub.Subscribe("child", _store.LastSeq("child"));
        Assert.Throws<InvalidOperationException>(() => RunRecovery.CloseUnfinished(_store, _hub, 222, _ => true, (_, _) => throw new InvalidOperationException("fault")));
        Assert.Single(_store.UnfinishedRuns()); Assert.Equal(["turn", "delta"], _store.RunRows("child", "r").Select(r => r.Kind));
        Assert.False(subscription.Rows.TryRead(out _));
        Assert.Equal(1, RunRecovery.CloseUnfinished(_store, _hub, 222, _ => true, (_, _) => { }));
    }
    [Fact]
    public void Recovery_admission_rechecks_terminal_and_saved_watermark_before_inserting()
    {
        Seed("partial"); var original = _store.RunRows("child", "r"); var recovered = new RecoveredChatTurn("child", "r", 1, original[^1].Seq, 222);
        var final = new RowBatch(); final.Rows.Add(new("r", null, "turn", JsonSerializer.SerializeToElement(new { state = "finished" }), 200));
        _store.Commit("child", final);
        Assert.Empty(_hub.PublishRecovered(recovered, RunRecovery.Close(original, 222), _ => throw new Exception("Already terminal")));
        Assert.Single(_store.RunRows("child", "r"), r => r.Kind == "turn" && r.Payload.GetProperty("state").GetString() != "started");
    }
    [Fact]
    public void Recovery_saved_watermark_cannot_omit_newer_committed_delta()
    {
        Seed("first"); var original = _store.RunRows("child", "r");
        var recovered = new RecoveredChatTurn("child", "r", 1, original[^1].Seq, 222);
        var newer = new RowBatch(); newer.Rows.Add(new("r", "assistant", "delta", JsonSerializer.SerializeToElement(new { text = "newer" }), 102));
        _store.Commit("child", newer);
        Assert.Throws<Assembly.Application.Common.Calls.CallFailed>(() => _hub.PublishRecovered(recovered, RunRecovery.Close(original, 222), _ => throw new Exception("Stale observer")));
        Assert.Single(_store.UnfinishedRuns()); Assert.Equal(2, _store.RunRows("child", "r").Count(r => r.Kind == "delta"));
        Assert.DoesNotContain(_store.RunRows("child", "r"), r => r.Kind == "error");
    }

    [Fact]
    public void Native_display_cleanup_keeps_original_locality_without_engine_or_session_info_rows()
    {
        var batch = new RowBatch();
        batch.Rows.Add(new(null, "subagent:native", "subagent", JsonSerializer.SerializeToElement(new { sessionId = "native", title = "Native", state = "running", canStop = true }), 100));
        _store.Commit("parent", batch);
        Assert.Equal(0, RunRecovery.CloseUnfinished(_store, _hub, 222, _ => true, (_, _) => throw new Exception("Display is not a helper terminal")));
        var last = _store.RowsAfter("parent", 0)[^1];
        Assert.Equal("idle", last.Payload.GetProperty("state").GetString()); Assert.False(last.Payload.GetProperty("canStop").GetBoolean());
        Assert.Equal("cancelled", last.Payload.GetProperty("stopReason").GetString());
    }
    public void Dispose() { _hub.Dispose(); _db.Dispose(); _file.Dispose(); }
}
