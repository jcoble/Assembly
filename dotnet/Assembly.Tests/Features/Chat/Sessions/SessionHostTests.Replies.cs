using Assembly.Domain.Rows;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Theory]
    [InlineData("finished", "final 世界\0", "final 世界\ufffd")]
    [InlineData("failed", "partial", "partial")]
    [InlineData("cancelled", "", "")]
    public async Task Actual_helper_foreground_terminal_flushes_exact_reply_independently_of_usage(string state, string body, string expected)
    {
        var child = _sessionStore.Helper(_session, "claude", "Captured child");
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work,
            feed: new EngineFeed(runtime, _log, _time));
        var host = registry.For(child.SessionId); var run = host.Send("work")!;
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        if (body.Length > 0) await agent.TextAsync(body);
        var fields = state == "failed" ? "{\"stopReason\":\"error\",\"error\":{\"code\":-32000,\"message\":\"Sign in\"}}"
            : "{\"stopReason\":\"" + (state == "cancelled" ? "cancelled" : "end_turn") + "\",\"usage\":{\"inputTokens\":-1}}";
        await agent.FinishAsync(prompt, fields); await agent.CloseAsync(); await Until(() => host.ActiveRunId is null);
        var rows = _store.RunRows(child.SessionId, run);
        Assert.Equal(state, rows.Last(r => r.Kind == "turn").Payload.GetProperty("state").GetString());
        var actual = _database.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE from_session_id=$id", r => new System.Text.UTF8Encoding(false, true).GetString(r.Blob(0)), default,
            new Assembly.Application.Common.Database.SqlArg("$id", child.SessionId));
        Assert.Equal(expected, actual);
        var assistant = rows.LastOrDefault(r => r.Kind == "message");
        Assert.Equal(expected, assistant is null ? "" : PlainText.Of(assistant.Payload.GetProperty("blocks"))); Assert.Equal(1, _database.QueryFirst("SELECT COUNT(*) FROM engine_message WHERE from_session_id=$id", r => r.Long(0), default,
            new Assembly.Application.Common.Database.SqlArg("$id", child.SessionId)));
        Assert.DoesNotContain("wasn't saved", _log.ToString());
    }

    [Fact]
    public async Task Actual_helper_failed_open_without_acp_or_provider_id_preserves_empty_terminal_reply()
    {
        var child = _sessionStore.Helper(_session, "claude", "Child");
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        _launcher.Launch = _ => throw new IOException("Open failed");
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work,
            feed: new EngineFeed(runtime, _log, _time));
        var host = registry.For(child.SessionId); var run = host.Send("work")!;
        await Until(() => host.ActiveRunId is null);
        Assert.Equal("failed", _store.RunRows(child.SessionId, run).Last(r => r.Kind == "turn").Payload.GetProperty("state").GetString());
        Assert.Equal("", _database.QueryFirst("SELECT text FROM engine_message", r => r.Text(0), default));
        Assert.Equal(0, _database.QueryFirst("SELECT COUNT(*) FROM engine_usage", r => r.Long(0), default));
    }
    [Theory]
    [InlineData("end")]
    [InlineData("failure")]
    [InlineData("quiet")]
    [InlineData("stop")]
    [InlineData("shutdown")]
    [InlineData("loss")]
    public async Task Actual_helper_self_started_terminal_keeps_its_original_chat_run_and_partial_reply(string ending)
    {
        var child = _sessionStore.Helper(_session, "claude", "Self child");
        await using var runtime = EngineRuntime.Start(_database, new Topics(), _time, new(() => 1, () => 5),
            cutover: new(_database, _sessionStore, _launcher, "checkouts", _time));
        await using var registry = new SessionRegistry(_hub, _sessionStore, _launcher, _time, _log, _work,
            feed: new EngineFeed(runtime, _log, _time));
        using var live = _hub.Subscribe(child.SessionId, 0);
        var host = registry.For(child.SessionId); var firstRun = host.Send("start background")!;
        var agent = await _launcher.NextAsync(); await agent.StartAsync(); var prompt = await agent.ReadAsync("session/prompt");
        await agent.UpdateAsync(Background); await agent.FinishAsync(prompt, "{\"stopReason\":\"end_turn\"}");
        await Next(r => r.Kind == "turn" && r.Payload.GetProperty("state").GetString() != "started");
        await Until(() => host.ActiveRunId is null);
        await agent.UpdateAsync("{\"sessionUpdate\":\"state_update\",\"state\":\"running\"}");
        await agent.TextAsync("Self partial 世界", "self-message");
        var delta = await Next(r => r.Kind == "delta"); var selfRun = delta.RunId!;
        Assert.NotEqual(firstRun, selfRun); Assert.Equal(selfRun, host.ActiveRunId);
        Task? closing = null;
        switch (ending)
        {
            case "end": await agent.UpdateAsync("{\"sessionUpdate\":\"state_update\",\"state\":\"idle\",\"stopReason\":\"end_turn\"}"); break;
            case "failure": await agent.UpdateAsync("{\"sessionUpdate\":\"state_update\",\"state\":\"idle\",\"stopReason\":\"error\",\"error\":{\"code\":-1,\"message\":\"failed\"}}"); break;
            case "quiet":
                await agent.UpdateAsync(NoBackground); await Next(r => r.Kind == "task" && r.Payload.GetProperty("state").GetString() == "completed");
                _time.Advance(TimeSpan.FromMilliseconds(SessionHost.QuietMs + 200)); break;
            case "stop": Assert.True(host.Stop(selfRun)); await agent.ReadAsync("session/cancel"); break;
            case "shutdown": closing = host.CloseKeptForShutdownAsync(); break;
            case "loss": agent.Crash(); break;
        }
        var terminal = await Next(r => r.Kind == "turn" && r.Payload.GetProperty("state").GetString() != "started");
        Assert.Equal(selfRun, terminal.RunId);
        Assert.Equal(ending is "failure" or "loss" ? "failed" : ending is "stop" or "shutdown" ? "cancelled" : "finished", terminal.Payload.GetProperty("state").GetString());
        var replies = _database.Query("SELECT CAST(text AS BLOB) FROM engine_message WHERE from_session_id=$id ORDER BY id", r => new System.Text.UTF8Encoding(false, true).GetString(r.Blob(0)), default,
            new Assembly.Application.Common.Database.SqlArg("$id", child.SessionId));
        Assert.Equal(2, replies.Count); Assert.Contains("", replies); Assert.Contains("Self partial 世界", replies);
        Assert.DoesNotContain("wasn't saved", _log.ToString());
        if (ending != "loss")
        {
            if (ending != "quiet") closing ??= host.CloseKeptForShutdownAsync();
            await agent.CloseAsync(); if (closing is not null) await closing.WaitAsync(Wait);
        }
        async Task<StoredRow> Next(Func<StoredRow, bool> take)
        {
            while (true) { var row = await live.Rows.ReadAsync().AsTask().WaitAsync(Wait); if (take(row)) return row; }
        }
    }

}
