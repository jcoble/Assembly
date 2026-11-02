using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine;

public sealed class DeliveryVersionTests
{
    private static readonly EngineActor System = new(ActorKind.System, "adapter");

    [Fact]
    public void Later_release_and_retry_advance_the_published_display_revision()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running");
        var send = f.Apply(new SendMessage(null, "s", "hello", "prompt"), "m");
        Assert.True(send.Applied);
        var queued = Row(f);
        f.Time.Advance(TimeSpan.FromSeconds(1));
        State(f, "idle");
        var released = Row(f);
        Assert.True(released.GetProperty("version").GetInt64() > queued.GetProperty("version").GetInt64());
        Assert.Equal(2000, released.GetProperty("updatedMs").GetInt64());
        AssertFinalEvent(f, "m");

        Assert.True(f.Apply(new ClaimEffect(), "send/m/1", actor: System).Applied);
        var outcome = f.Apply(Queued("busy"), "send/m/1", 1, actor: System);
        Assert.True(outcome.Applied); Assert.Equal(f.Number("SELECT version FROM engine_outbox WHERE id='send/m/1'"), outcome.Version);
        var returned = Row(f);
        f.Time.Advance(TimeSpan.FromSeconds(1));
        State(f, "running"); State(f, "idle");
        var retry = Row(f);
        Assert.True(retry.GetProperty("version").GetInt64() > returned.GetProperty("version").GetInt64());
        Assert.Equal(3000, retry.GetProperty("updatedMs").GetInt64());
        Assert.Equal(2, f.Number("SELECT attempt FROM engine_message WHERE id='m'"));
        AssertFinalEvent(f, "m");
    }

    [Fact]
    public void Immediate_send_and_same_message_rerelease_return_one_final_event_and_replay_exactly()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var id = Guid.NewGuid(); var send = new SendMessage(null, "s", "hello", "prompt");
        var inserted = f.Apply(send, "m", commandId: id);
        Assert.True(inserted.Applied); Assert.Equal(2, inserted.Version);
        Assert.Equal(inserted.Version, AssertFinalEvent(f, "m").Version);
        Assert.Single(f.Events(), e => e.EntityKind == "message");
        Assert.Equal(inserted, f.Apply(send, "m", commandId: id));
        var before = f.Events().Last().Seq;
        Assert.True(f.Apply(new ClaimEffect(), "send/m/1", actor: System).Applied);
        id = Guid.NewGuid(); var command = Queued("not_delivered");
        var result = f.Apply(command, "send/m/1", 1, actor: System, commandId: id);
        Assert.True(result.Applied); Assert.Equal(2, result.Version);
        Assert.Equal(4, Row(f).GetProperty("version").GetInt64());
        var item = Assert.Single(f.Events(), e => e.Seq > before && e.EntityKind == "message");
        Assert.Equal(4, item.Version); AssertFinalEvent(f, "m");
        var count = f.Count("engine_event"); var effects = f.Count("engine_outbox");
        f.Reopen(); Assert.Equal(result, f.Apply(command, "send/m/1", 1, actor: System, commandId: id));
        Assert.Equal(count, f.Count("engine_event")); Assert.Equal(effects, f.Count("engine_outbox"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Held_release_and_child_note_publish_only_the_committed_message_revision(bool childNote)
    {
        using var f = new EngineFixture(); f.SeedSession();
        long before;
        string message;
        if (childNote)
        {
            f.SeedSession("child", parentId: "s"); before = 0; message = "child-ended/child";
            Assert.True(f.Apply(new SetSessionState("ended", null, null), "child", actor: System).Applied);
        }
        else
        {
            Assert.True(f.Apply(new SendMessage(null, "s", "hello", "interrupt"), "m").Applied);
            before = f.Events().Last().Seq; message = "m"; State(f, "idle");
        }
        var item = Assert.Single(f.Events(), e => e.Seq > before && e.EntityKind == "message");
        Assert.Equal(message, item.EntityId); AssertFinalEvent(f, message);
        Assert.Equal(childNote ? 2 : 3, item.Version);
    }

    private static CompleteEffect Queued(string reason) => new(new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(new MessageEffectData("queued", null, reason),
            Assembly.Engine.EngineJson.Default.MessageEffectData), null));

    private static EngineEvent AssertFinalEvent(EngineFixture f, string id)
    {
        var item = f.Events().Last(e => e.EntityKind == "message" && e.EntityId == id);
        Assert.Equal(f.Number("SELECT version FROM engine_message WHERE id=$id", new SqlArg("$id", id)), item.Version);
        return item;
    }
    private static void State(EngineFixture f, string state) => Assert.True(f.Apply(new SetSessionState(state, null, null), "s",
        f.Number("SELECT version FROM engine_session WHERE id='s'"), actor: System).Applied);
    private static JsonElement Row(EngineFixture f) => new EngineReader(f.Db, f.Machine).Entity("message", "m", default)!.Value;
}
