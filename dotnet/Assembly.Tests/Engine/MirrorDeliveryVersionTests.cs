using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine;

public sealed class MirrorDeliveryVersionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly EngineActor Adapter = new(ActorKind.System, "adapter");

    [Fact]
    public void Actual_mirror_tracks_later_release_and_coalesced_rerelease_with_receipt_replay()
    {
        using var owner = new EngineFixture();
        using var mirror = new EngineFixture();
        Assert.NotEqual(owner.Machine, mirror.Machine);
        owner.SeedSession(state: "running"); // can_steer defaults to false: no release while busy.
        Assert.Equal(0, owner.Number("SELECT can_steer FROM engine_session WHERE id='s'"));
        var reader = new EngineReader(owner.Db, owner.Machine);
        var mirroredReader = new EngineReader(mirror.Db, mirror.Machine);
        var send = new SendMessage(null, "s", "Queued while the recipient is busy", "prompt");
        var sendContext = Context(owner, "m", 0);
        var sent = owner.Apply(sendContext, send);
        Assert.True(sent.Applied);
        Assert.Equal(1, sent.Version);
        Assert.Equal(0, owner.Count("engine_outbox"));
        Assert.Equal(0, owner.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        var queued = reader.Entity("message", "m", default)!.Value;
        Assert.Equal("queued", queued.GetProperty("state").GetString());

        mirror.Db.Writer.Write("test.mirror-open", s => MirrorApply.Open(s, owner.Machine, "workbox-test", 1000));
        MirrorCursor cursor;
        mirror.Db.Writer.Write("test.mirror-begin", s => MirrorApply.BeginSnapshot(s, owner.Machine, 1000));
        using (var snapshot = reader.Snapshot("mirror", null, default))
        {
            SnapshotPage page;
            do
            {
                page = snapshot.ReadNext();
                mirror.Db.Writer.Write("test.mirror-snapshot", s => { MirrorApply.SnapshotPage(s, owner.Machine, page.Items); return 0; });
            } while (page.HasMore);
            cursor = mirror.Db.Writer.Write("test.mirror-end", s => MirrorApply.EndSnapshot(s, owner.Machine, page.Through, page.At, 1000));
        }
        Assert.Equal(queued.GetRawText(), mirroredReader.Entity("message", "m", default, owner.Machine)!.Value.GetRawText());
        Assert.Equal(reader.HighWatermark(default), cursor.Through);
        Assert.Equal(reader.EventAt(cursor.Through, default), cursor.At);
        output.WriteLine($"Queued owner/mirror: version={sent.Version}, updatedMs={queued.GetProperty("updatedMs").GetInt64()}, through={cursor.Through}, at={cursor.At}");

        owner.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(owner.Apply(new SetSessionState("idle", null, null), "s", actor: Adapter).Applied);
        Assert.Equal(1, owner.Count("engine_outbox"));
        var released = reader.Entity("message", "m", default)!.Value;
        Assert.Equal(2, released.GetProperty("version").GetInt64());
        Assert.Equal(2000, released.GetProperty("updatedMs").GetInt64());
        Assert.Equal(1, owner.Number("SELECT in_flight FROM engine_message WHERE id='m'"));
        cursor = ApplyPage(owner, mirror, reader, mirroredReader, cursor, 2, 2000);
        output.WriteLine($"Idle release owner/mirror: version=2, updatedMs=2000, through={cursor.Through}, at={cursor.At}");

        var eventCount = owner.Count("engine_event");
        Assert.Equal(sent, owner.Apply(sendContext, send)); // original v1 receipt, current display stays v2.
        Assert.Equal(eventCount, owner.Count("engine_event"));
        Assert.Equal(2, reader.Entity("message", "m", default)!.Value.GetProperty("version").GetInt64());
        Assert.Empty(reader.Since("mirror", null, cursor.Through, default).Events);

        owner.Time.Advance(TimeSpan.FromSeconds(1));
        var outcomeContext = Context(owner, "m", 2, Adapter);
        var outcome = new MessageOutcome("queued", null, "not_delivered");
        var rereleased = owner.Apply(outcomeContext, outcome);
        Assert.True(rereleased.Applied);
        Assert.Equal(4, rereleased.Version); // outcome v3 and the same message's release v4 commit one final draft.
        Assert.Equal(2, owner.Count("engine_outbox"));
        Assert.Equal(2, owner.Number("SELECT attempt FROM engine_message WHERE id='m'"));
        cursor = ApplyPage(owner, mirror, reader, mirroredReader, cursor, 4, 3000);
        output.WriteLine($"Coalesced outcome+release owner/mirror: version=4, updatedMs=3000, through={cursor.Through}, at={cursor.At}");
        var ownerEvents = owner.Count("engine_event");
        var mirrorEvents = mirror.Count("engine_event");
        owner.Reopen();
        reader = new EngineReader(owner.Db, owner.Machine);
        Assert.Equal(rereleased, owner.Apply(outcomeContext, outcome));
        Assert.Equal(ownerEvents, owner.Count("engine_event"));
        Assert.Equal(2, owner.Count("engine_outbox"));
        var empty = reader.Since("mirror", null, cursor.Through, default);
        Assert.Empty(empty.Events);
        cursor = mirror.Db.Writer.Write("test.mirror-replayed-receipt", s => MirrorApply.Events(s, owner.Machine,
            empty.After, empty.Through, empty.At, empty.Events, empty.Rows!, 3000));
        Assert.Equal(mirrorEvents, mirror.Count("engine_event"));
        Assert.Equal(reader.HighWatermark(default), cursor.Through);
        Assert.Equal(reader.EventAt(cursor.Through, default), cursor.At);
        Assert.Equal(0, mirror.Count("engine_message"));
        Assert.Equal(0, mirror.Count("engine_outbox"));
        Assert.Equal(0, mirror.Count("engine_command_receipt"));
        output.WriteLine($"Original receipts replay exactly: Send version={sent.Version}, outcome version={rereleased.Version}; no new owner/mirror events or mirror effects/receipts.");
    }

    private static MirrorCursor ApplyPage(EngineFixture owner, EngineFixture mirror, EngineReader reader,
        EngineReader mirroredReader, MirrorCursor cursor, long version, long updatedMs)
    {
        var page = reader.Since("mirror", null, cursor.Through, default);
        Assert.False(page.SnapshotRequired);
        Assert.False(page.HasMore);
        Assert.Equal(cursor.Through, page.After);
        var messageEvent = Assert.Single(page.Events, e => e.EntityKind == "message" && e.EntityId == "m");
        Assert.Equal(version, messageEvent.Version);
        var row = Assert.Single(page.Rows!, r => r.GetProperty("entityKind").GetString() == "message");
        Assert.Equal(version, row.GetProperty("version").GetInt64());
        Assert.Equal(updatedMs, row.GetProperty("updatedMs").GetInt64());
        cursor = mirror.Db.Writer.Write("test.mirror-events", s => MirrorApply.Events(s, owner.Machine,
            page.After, page.Through, page.At, page.Events, page.Rows!, updatedMs));
        var actual = mirroredReader.Entity("message", "m", default, owner.Machine)!.Value;
        Assert.Equal(reader.Entity("message", "m", default)!.Value.GetRawText(), actual.GetRawText());
        Assert.Equal(version, actual.GetProperty("version").GetInt64());
        Assert.Equal(updatedMs, actual.GetProperty("updatedMs").GetInt64());
        Assert.Equal("queued", actual.GetProperty("state").GetString());
        Assert.Equal(page.Through, cursor.Through);
        Assert.Equal(page.At, cursor.At);
        Assert.Equal(reader.EventAt(cursor.Through, default), mirroredReader.EventAt(cursor.Through, default, owner.Machine));
        var replay = mirroredReader.Since("messages", "m", page.After, default, source: owner.Machine);
        Assert.Equal(messageEvent, Assert.Single(replay.Events));
        return cursor;
    }

    private static CommandContext Context(EngineFixture owner, string id, long version, EngineActor? actor = null) =>
        new(Guid.NewGuid(), owner.Machine, actor ?? new(ActorKind.Window, "window"), id, version, 0,
            owner.Time.GetUtcNow().ToUnixTimeMilliseconds());
}
