using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Execution;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Capabilities;

public sealed class SessionSteeringDisplayTests
{
    private static readonly EngineActor Adapter = new(ActorKind.System, "adapter");

    [Theory]
    [InlineData("idle")]
    [InlineData("running")]
    public void Default_capability_is_false_regardless_of_running_state_and_display_grants_no_admission(string state)
    {
        using var f = new EngineFixture(); f.SeedSession(state: state);
        var reader = new EngineReader(f.Db, f.Machine);
        Assert.False(reader.Entity("session", "s", default)!.Value.GetProperty("canSteer").GetBoolean());
        Assert.False(SessionSnapshot(reader, "sessions").GetProperty("canSteer").GetBoolean());
        Assert.Equal("forbidden", f.Apply(new ReportCapabilities(true), "s").Error);
        Assert.False(reader.Entity("session", "s", default)!.Value.GetProperty("canSteer").GetBoolean());
        Assert.Empty(f.Events());
    }

    [Fact]
    public void Actual_capability_change_publishes_invalidation_and_all_display_reads_agree_at_that_version()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var reader = new EngineReader(f.Db, f.Machine);
        var publisher = new EnginePublisher(f.Db);
        List<EngineEvent> pushed = [];
        using var subscription = publisher.Register(pushed.Add, out _);

        Assert.True(f.Apply(new ReportCapabilities(true), "s", actor: Adapter).Applied);
        publisher.Drain(default);
        var first = Assert.Single(pushed);
        Assert.Equal("session.changed", first.Kind); Assert.Equal(f.Machine, first.Machine);
        var rehydrated = Assert.Single(reader.Entities([new(first.EntityKind, first.EntityId)], null, default).Items);
        Assert.True(rehydrated.GetProperty("canSteer").GetBoolean());
        Assert.Equal(first.Version, rehydrated.GetProperty("version").GetInt64());
        Assert.Equal(rehydrated.GetRawText(), reader.Entity("session", "s", default)!.Value.GetRawText());
        Assert.Equal(rehydrated.GetRawText(), SessionSnapshot(reader, "sessions").GetRawText());
        Assert.True(Assert.Single(reader.Since("mirror", null, 0, default).Rows!).GetProperty("canSteer").GetBoolean());

        Assert.True(f.Apply(new ReportCapabilities(true), "s", first.Version, actor: Adapter).Applied);
        publisher.Drain(default); Assert.Single(pushed);
        Assert.True(f.Apply(new ReportCapabilities(false), "s", first.Version, actor: Adapter).Applied);
        publisher.Drain(default); Assert.Equal(2, pushed.Count);
        var last = pushed[^1];
        Assert.True(last.Version > first.Version);
        var changed = Assert.Single(reader.Entities([new(last.EntityKind, last.EntityId)], null, default).Items);
        Assert.False(changed.GetProperty("canSteer").GetBoolean());
        Assert.Equal(last.Version, changed.GetProperty("version").GetInt64());
        Assert.False(SessionSnapshot(reader, "sessions").GetProperty("canSteer").GetBoolean());
        Assert.False(Assert.Single(reader.Since("mirror", null, first.Seq, default).Rows!).GetProperty("canSteer").GetBoolean());
        Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void Existing_mirror_snapshot_preserves_actual_owner_and_typed_capability()
    {
        using var owner = new EngineFixture(); using var target = new EngineFixture(); owner.SeedSession();
        Assert.True(owner.Apply(new ReportCapabilities(true), "s", actor: Adapter).Applied);
        target.Db.Writer.Write("mirror.open", scope => MirrorApply.Open(scope, owner.Machine, "saved-route", 1000));
        target.Db.Writer.Write("mirror.begin", scope => MirrorApply.BeginSnapshot(scope, owner.Machine, 1000));
        using var snapshot = new EngineReader(owner.Db, owner.Machine).Snapshot("mirror", null, default);
        SnapshotPage page;
        do
        {
            page = snapshot.ReadNext();
            target.Db.Writer.Write("mirror.page", scope => { MirrorApply.SnapshotPage(scope, owner.Machine, page.Items); return 0; });
        } while (page.HasMore);
        target.Db.Writer.Write("mirror.end", scope => MirrorApply.EndSnapshot(scope, owner.Machine, page.Through, page.At, 1000));
        var remote = new EngineReader(target.Db, target.Machine).Entity("session", "s", default, owner.Machine)!.Value;
        Assert.Equal(owner.Machine, remote.GetProperty("machine").GetString());
        Assert.True(remote.GetProperty("canSteer").GetBoolean());
        Assert.Equal(1, remote.GetProperty("version").GetInt64());
    }

    private static JsonElement SessionSnapshot(EngineReader reader, string topic)
    {
        using var snapshot = reader.Snapshot(topic, null, default);
        var page = snapshot.ReadNext();
        Assert.False(page.HasMore);
        return Assert.Single(page.Items, item => item.GetProperty("entityKind").GetString() == "session");
    }
}
