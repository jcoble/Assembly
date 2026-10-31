using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Application.Features.Engine.Messages;

namespace Assembly.Tests.Engine;

public sealed class SessionTests
{
    private static StartSession Start(string? parent = null, string? run = null) =>
        new("codex", "m", "/repo", "x", parent, run, null);

    [Fact]
    public void Child_cannot_start_grandchild()
    {
        using var f = new EngineFixture();
        f.SeedSession("root"); f.SeedSession("child", parentId: "root");
        var r = f.Apply(Start("child"), "grandchild", actor: new(ActorKind.Session, "child"));
        Assert.Equal("ask your starter to start it", r.Error);
        Assert.Equal(2, f.Count("engine_session")); Assert.Empty(f.Events());
    }

    [Fact]
    public void Stop_waits_for_adapter_ack()
    {
        using var f = new EngineFixture(); f.SeedSession("s", "running");
        Assert.True(f.Apply(new StopSession(), "s").Applied);
        Assert.Equal("running", f.State("engine_session", "s"));
        Assert.Equal(1, f.Number("SELECT cancel_requested FROM engine_session"));
        Assert.Equal("stop", f.Text("SELECT kind FROM engine_outbox"));
        Assert.True(f.Apply(new SetSessionState("ended", null, null), "s", version: 1,
            actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("ended", f.State("engine_session", "s"));
    }

    [Theory]
    [InlineData("idle", "idle")]
    [InlineData("running", "idle")]
    [InlineData("running", "ended")]
    public void Stop_ack_clears_cancel_and_releases_held_messages(string initial, string acknowledgement)
    {
        using var f = new EngineFixture(); f.SeedSession("s", initial);
        Assert.True(f.Apply(new StopSession(), "s").Applied);
        Assert.True(f.Apply(new SendMessage(null, "s", "hello", "interrupt"), "m").Applied);
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='stop'"));
        Assert.True(f.Apply(new SetSessionState(acknowledgement, null, null), "s", version: 1,
            actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal(0, f.Number("SELECT cancel_requested FROM engine_session WHERE id='s'"));
        Assert.Equal(acknowledgement == "idle" ? "queued" : "failed", f.State("engine_message", "m"));
        Assert.Equal(acknowledgement == "idle" ? null : "recipient_ended", f.Text("SELECT reason FROM engine_message"));
        Assert.Equal(acknowledgement == "idle" ? 1 : 0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
        if (acknowledgement == "idle")
        {
            Assert.Equal("invalid_transition", f.Apply(new SetSessionState("idle", null, null), "s", version: 2,
                actor: new(ActorKind.System, "adapter")).Error);
            Assert.True(f.Apply(new MessageOutcome("delivered_to_turn", "turn-1", null), "m", version: 2,
                actor: new(ActorKind.System, "adapter")).Applied);
        }
    }

    public static IEnumerable<object[]> Edges()
    {
        string[] states = ["starting", "idle", "running", "lost", "ended"];
        foreach (var from in states)
            foreach (var to in states)
                yield return [from, to, (from, to) is ("starting", "idle") or ("idle", "running") or
                ("running", "idle") or ("starting" or "idle" or "running", "lost" or "ended") or ("lost", "idle")];
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public void Allowed_and_refused_session_edges(string from, string to, bool allowed)
    {
        using var f = new EngineFixture(); f.SeedSession("s", from);
        var result = f.Apply(new SetSessionState(to, "provider-id", null), "s", actor: new(ActorKind.System, "adapter"));
        Assert.Equal(allowed, result.Applied);
        Assert.Equal(allowed ? null : "invalid_transition", result.Error);
        Assert.Equal(allowed ? to : from, f.State("engine_session", "s"));
        Assert.Equal(allowed ? 1 : 0, f.Number("SELECT version FROM engine_session"));
        Assert.Equal(allowed ? 1 : 0, f.Count("engine_event"));
    }

    [Fact]
    public async Task Concurrent_children_obey_setting()
    {
        using var f = new EngineFixture(childLimit: 2); f.SeedSession("root");
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Task.Run(() =>
            f.Apply(Start("root"), $"child-{i}", actor: new(ActorKind.Session, "root")))));
        Assert.Equal(2, results.Count(r => r.Applied));
        Assert.Equal("child_limit", Assert.Single(results, r => !r.Applied).Error);
        var child = f.Text("SELECT id FROM engine_session WHERE parent_id='root' LIMIT 1")!;
        Assert.True(f.Apply(new SetSessionState("ended", null, null), child, version: 1,
            actor: new(ActorKind.System, "adapter")).Applied);
        Assert.True(f.Apply(Start("root"), "replacement", actor: new(ActorKind.Session, "root")).Applied);
    }

    [Fact]
    public void Starter_run_epoch_and_legacy_identity()
    {
        using var f = new EngineFixture();
        f.SeedSession("root"); f.SeedSession("other"); f.SeedRun(ownerSessionId: "root");
        Assert.Equal("forbidden", f.Apply(Start("root"), "forged", actor: new(ActorKind.Session, "other")).Error);
        Assert.Equal("forbidden", f.Apply(Start("other", "r"), "wrong-run").Error);
        Assert.True(f.Apply(Start("root", "r"), "valid", actor: new(ActorKind.Session, "root")).Applied);
        f.SeedSession("fenced", epoch: 2);
        var stale = f.Apply(new StopSession(), "fenced", epoch: 1);
        Assert.Equal("stale_epoch", stale.Error); Assert.Equal(2, stale.Epoch);
        f.Db.Writer.Write("legacy", s => s.Execute("INSERT INTO sessions(session_id,next_seq) VALUES('legacy',7)"));
        Assert.Equal("legacy_session_not_managed", f.Apply(new StopSession(), "legacy").Error);
        Assert.Equal("legacy_session_not_managed", f.Apply(Start(), "legacy").Error);
        Assert.Equal(7, f.Number("SELECT next_seq FROM sessions WHERE session_id='legacy'"));
    }

    [Fact]
    public void Start_is_durable_and_replayed_without_duplicate_effects()
    {
        using var f = new EngineFixture(); var id = Guid.NewGuid();
        var command = Start() with { TaskUrl = "https://example.com/task" };
        var result = f.Apply(command, "s", commandId: id);
        Assert.True(result.Applied); Assert.Equal(1, result.Version);
        Assert.Equal("starting", f.State("engine_session", "s"));
        Assert.Equal("start", f.Text("SELECT kind FROM engine_outbox"));
        Assert.Equal("https://example.com/task", f.Text("SELECT task_url FROM engine_session"));
        f.Reopen(); Assert.Equal(result, f.Apply(command, "s", commandId: id));
        Assert.Single(f.Events()); Assert.Equal(1, f.Count("engine_outbox"));
        Assert.Equal("command_id_conflict", f.Apply(command with { Title = "changed" }, "s", commandId: id).Error);
    }

    [Fact]
    public void Invalid_arguments_links_and_actors_do_not_mutate()
    {
        using var f = new EngineFixture(); f.SeedSession();
        foreach (var command in new[] { Start() with { Provider = " " }, Start() with { Model = "" },
                     Start() with { Folder = "" }, Start() with { Title = "" }, Start("missing"), Start("s", "missing") })
            Assert.Equal("bad_args", f.Apply(command, Guid.NewGuid().ToString()).Error);
        Assert.Equal("forbidden", f.Apply(Start(), "root", actor: new(ActorKind.Session, "s")).Error);
        Assert.Equal("forbidden", f.Apply(Start(), "root", actor: new(ActorKind.System, "adapter")).Error);
        Assert.Equal("forbidden", f.Apply(new SetSessionState("running", null, null), "s").Error);
        Assert.Equal("forbidden", f.Apply(new StopSession(), "s", actor: new(ActorKind.Session, "other")).Error);
        Assert.Equal("version_conflict", f.Apply(new StopSession(), "s", version: 1).Error);
        Assert.Empty(f.Events()); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Missing_state_is_bad_args_and_ended_refuses_stop()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedSession("done", "ended");
        Assert.Equal("bad_args", f.Apply(new SetSessionState("", null, null), "s",
            actor: new(ActorKind.System, "adapter")).Error);
        Assert.Equal("invalid_transition", f.Apply(new StopSession(), "done").Error);
        Assert.Empty(f.Events()); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Open_needs_preserve_activity_and_resume_restores_provider_identity()
    {
        using var f = new EngineFixture(); f.SeedSession();
        f.Db.Writer.Write("need", s => s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state)
            VALUES('need',0,$machine,1000,1000,'s','question','Help','[]','open')
            """, new SqlArg("$machine", f.Machine)));
        Assert.True(f.Apply(new SetSessionState("running", "provider", null), "s", actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("needs_me", f.State("engine_session", "s"));
        Assert.Equal("running", f.Text("SELECT activity_state FROM engine_session"));
        Assert.True(f.Apply(new SetSessionState("lost", null, "disconnected"), "s", version: 1,
            actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal(1000, f.Number("SELECT lost_ms FROM engine_session"));
        Assert.True(f.Apply(new SetSessionState("idle", "resumed", null), "s", version: 2,
            actor: new(ActorKind.System, "adapter")).Applied);
        Assert.Equal("needs_me", f.State("engine_session", "s"));
        Assert.Equal("idle", f.Text("SELECT activity_state FROM engine_session"));
        Assert.Equal("resumed", f.Text("SELECT provider_session_id FROM engine_session"));
        Assert.Null(f.Text("SELECT lost_ms FROM engine_session"));
    }
}
