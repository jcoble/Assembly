using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Engine;

public sealed class EngineFeedTests
{
    [Fact]
    public async Task State_change_after_a_version_bump_still_applies()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        Assert.True(runtime.SubmitCurrent("session", "s", new SetSessionState("running", null, null)).Applied);
        Assert.True(f.Apply(new ReportCapabilities(true), "s", 1,
            actor: new EngineActor(ActorKind.System, "initialize")).Applied);
        Assert.True(runtime.SubmitCurrent("session", "s", new SetSessionState("idle", null, null)).Applied);
        Assert.Equal("idle", f.State("engine_session", "s"));
        Assert.Equal(3, f.Number("SELECT version FROM engine_session WHERE id='s'"));
    }

    [Fact]
    public async Task Current_submission_does_not_treat_a_nonlocal_or_missing_row_as_local()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession();
        f.Db.Writer.Write("test.foreign", s => s.Execute("UPDATE engine_session SET machine='other' WHERE id='s'"));
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        Assert.False(runtime.SubmitCurrent("session", "s", new SetSessionState("running", null, null)).Applied);
        Assert.False(runtime.SubmitCurrent("session", "missing", new ReportCapabilities(true)).Applied);
        Assert.Equal("idle", f.State("engine_session", "s"));
        Assert.Equal(0, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_command_receipt"));
    }

    [Fact]
    public void Capabilities_cannot_mutate_terminal_or_nonlocal_sessions()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "ended");
        var actor = new EngineActor(ActorKind.System, "adapter");
        Assert.Equal("invalid_transition", f.Apply(new ReportCapabilities(true), "s", actor: actor).Error);
        f.Db.Writer.Write("test.nonlocal", s => s.Execute("UPDATE engine_session SET state='idle',machine='other' WHERE id='s'"));
        Assert.Equal("wrong_machine", f.Apply(new ReportCapabilities(true), "s", actor: actor).Error);
        Assert.Equal(0, f.Number("SELECT can_steer FROM engine_session"));
        Assert.Equal(0, f.Count("engine_event"));
    }

    [Fact]
    public void Capabilities_are_system_only_and_do_not_repeat_unchanged_facts()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.Equal("forbidden", f.Apply(new ReportCapabilities(true), "s").Error);
        var actor = new EngineActor(ActorKind.System, "adapter");
        Assert.True(f.Apply(new ReportCapabilities(true), "s", actor: actor).Applied);
        Assert.Equal(1, f.Number("SELECT can_steer FROM engine_session"));
        Assert.Equal(1, f.Count("engine_event"));
        Assert.True(f.Apply(new ReportCapabilities(true), "s", 1, actor: actor).Applied);
        Assert.Equal(1, f.Count("engine_event"));
        Assert.True(f.Apply(new ReportCapabilities(false), "s", 1, actor: actor).Applied);
        Assert.Equal(0, f.Number("SELECT can_steer FROM engine_session"));
        Assert.Equal(2, f.Count("engine_event"));
        Assert.Equal(0, f.Count("engine_outbox"));
        var json = JsonSerializer.Serialize(new ReportCapabilities(true), EngineJson.Default.EngineCommand);
        Assert.IsType<ReportCapabilities>(JsonSerializer.Deserialize(json, EngineJson.Default.EngineCommand));
    }
}
