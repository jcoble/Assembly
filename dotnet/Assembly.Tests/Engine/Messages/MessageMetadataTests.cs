using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mirror;
using Assembly.Engine.Persistence;
using Assembly.Engine.Reading;
using Assembly.Engine.Messages.Reading;

namespace Assembly.Tests.Engine.Messages;

public sealed class MessageMetadataTests
{
    private static readonly MessageScope All = new(null, null, null, null, null, null, "", "time");

    [Fact]
    public void Copies_keep_origin_creation_order_and_only_the_actually_observed_recipient_outcome()
    {
        using var a = new EngineFixture(); using var b = new EngineFixture(); using var c = new EngineFixture();
        MessageReadFixture.Seed(a, "m", 100, recipientMachine: b.Machine);
        MessageReadFixture.Seed(c, "m", 200);
        MessageReadFixture.Mirror(a, c);
        var query = new MessageQueries(a.Db, a.Machine);
        Assert.Equal("queued", Assert.Single(query.Candidates(All, null, 200, default).Items, item => item.Key.OriginMachine == a.Machine).State);
        MessageReadFixture.Seed(b, "m", 900, origin: a.Machine, state: "next_turn");
        MessageReadFixture.Mirror(a, b);
        var rows = query.Candidates(All, null, 200, default).Items;
        Assert.Equal(2, rows.Count);
        var relayed = Assert.Single(rows, item => item.Key.OriginMachine == a.Machine);
        Assert.Equal(100, relayed.CreatedMs); Assert.Equal("next_turn", relayed.State); Assert.Equal(b.Machine, relayed.Storage.Machine);
        Assert.Equal(c.Machine, rows[0].Key.OriginMachine);
        MessageReadFixture.Mirror(a, b);
        Assert.Equal(2, query.Candidates(All, null, 200, default).Items.Count);
    }

    [Fact]
    public void Session_and_run_owner_scope_is_source_qualified_and_unknown_provenance_is_partial()
    {
        using var a = new EngineFixture(); using var b = new EngineFixture();
        MessageReadFixture.Seed(a, "local", 100);
        MessageReadFixture.Seed(b, "remote", 200);
        MessageReadFixture.Mirror(a, b);
        var query = new MessageQueries(a.Db, a.Machine);
        var selected = All with { SessionMachine = a.Machine, SessionId = "to" };
        Assert.Equal("local", Assert.Single(query.Candidates(selected, null, 200, default).Items).Key.Id);
        a.SeedSession("run-owner"); a.SeedRun("run", "run-owner");
        a.Db.Writer.Write("run binding", s => s.Execute("UPDATE engine_message SET run_id='run' WHERE id='local'"));
        Assert.Equal("local", Assert.Single(query.Candidates(All with { SessionMachine = a.Machine, SessionId = "run-owner" }, null, 200, default).Items).Key.Id);
        Assert.Empty(query.Candidates(All with { SessionMachine = b.Machine, SessionId = "run-owner" }, null, 200, default).Items);
        a.Db.Writer.Write("unknown provenance", s => s.Execute("UPDATE engine_message SET origin_machine=NULL WHERE id='local'"));
        var incomplete = query.Candidates(All, null, 200, default);
        Assert.DoesNotContain(incomplete.Items, item => item.Key.Id == "local");
        Assert.Contains(new MessageStorage(a.Machine, "local"), incomplete.SourceUnknown);
        Assert.True(query.Count(new(a.Machine, "to"), [], default).Partial);
        // Source-derived older display projection: the actual snapshot lacks only the new delivery field.
        MessageReadFixture.Mirror(a, b, omitDelivery: true);
        var legacy = query.Candidates(All, null, 200, default);
        Assert.DoesNotContain(legacy.Items, item => item.Key.OriginMachine == b.Machine && item.Key.Id == "remote");
        Assert.Contains(new MessageStorage(b.Machine, "remote"), legacy.SourceUnknown);
        Assert.True(query.Count(new(b.Machine, "to"), [], default).Partial);
    }

    [Fact]
    public void Current_source_qualified_recipient_titles_follow_session_only_mirror_events_and_invalidate_old_name_cursors()
    {
        using var local = new EngineFixture(); using var owner = new EngineFixture();
        MessageReadFixture.Seed(local, "local", 50); MessageReadFixture.Seed(owner, "first", 100); MessageReadFixture.Seed(owner, "second", 200);
        local.Db.Writer.Write("colliding local title", scope => scope.Execute("UPDATE engine_session SET title='Wrong local title' WHERE id='to'"));
        owner.Db.Writer.Write("original remote title", scope => scope.Execute("UPDATE engine_session SET title='Remote original' WHERE id='to'"));
        MessageReadFixture.Mirror(local, owner);
        var query = new MessageQueries(local.Db, local.Machine); var scope = All with { Machine = owner.Machine, Sort = "receiver" };
        var first = query.Candidates(scope, null, 1, default); Assert.Equal("Remote original", Assert.Single(first.Items).ReceiverName); Assert.NotNull(first.Next);
        var after = owner.Number("SELECT next_seq-1 FROM engine_meta");
        owner.Db.Writer.Write("rename recipient", write =>
        {
            write.Execute("UPDATE engine_session SET title='Remote renamed',version=version+1 WHERE id='to'");
            return EngineJournal.Commit(write, new(Guid.NewGuid(), owner.Machine, new(ActorKind.System, "fixture"), "to", 0, 0, 1001), 1, 0,
                [new("session.changed", "session", "to", 1, false, new("to", null, null, null, "idle", 0))], []);
        });
        var page = new EngineReader(owner.Db, owner.Machine).Since("mirror", null, after, default);
        local.Db.Writer.Write("session-only mirror update", write => MirrorApply.Events(write, owner.Machine, page.After, page.Through, page.At, page.Events, page.Rows ?? [], 1001));
        Assert.Throws<InvalidOperationException>(() => query.Candidates(scope, MessageQueries.DecodeCursor(first.Next!), 1, default));
        Assert.All(query.Candidates(scope, null, 200, default).Items, item => Assert.Equal("Remote renamed", item.ReceiverName));
        Assert.Equal(1, local.Number("SELECT version FROM engine_mirror_row WHERE machine=$machine AND entity_kind='message' AND entity_id='first'", new SqlArg("$machine", owner.Machine)));
        var searching = scope with { Search = "renamed" }; var candidates = query.Candidates(searching, null, 200, default);
        Assert.Equal(2, query.SelectMatches(searching, candidates, [], default).Items.Count);
    }

    [Fact]
    public void Equal_visible_name_previews_use_creation_and_identity_ties_with_a_stable_cursor()
    {
        using var f = new EngineFixture();
        var visible = new string('n', 256);
        MessageReadFixture.Seed(f, "older", 100, sender: visible + "z full tail");
        MessageReadFixture.Seed(f, "newer", 200, sender: visible + "z full tail");
        var query = new MessageQueries(f.Db, f.Machine);
        var scope = All with { Sort = "sender" }; var first = query.Candidates(scope, null, 1, default);
        var newer = Assert.Single(first.Items); Assert.Equal("newer", newer.Key.Id); Assert.Equal(visible, newer.SenderName);
        Assert.Equal("older", Assert.Single(query.Candidates(scope, MessageQueries.DecodeCursor(first.Next!), 1, default).Items).Key.Id);
        Assert.Equal(visible + "z full tail", query.Body("newer", default).GetProperty("senderName").GetString());
    }

    [Fact]
    public void Cursor_preserves_keyset_ties_but_refuses_changed_query_or_replaced_mirror_generation()
    {
        using var a = new EngineFixture(); using var b = new EngineFixture();
        MessageReadFixture.Seed(a, "a", 100); MessageReadFixture.Seed(a, "b", 100); MessageReadFixture.Seed(b, "a", 100);
        MessageReadFixture.Mirror(a, b);
        var query = new MessageQueries(a.Db, a.Machine); var first = query.Candidates(All, null, 1, default);
        Assert.NotNull(first.Next);
        var second = query.Candidates(All, MessageQueries.DecodeCursor(first.Next!), 1, default);
        Assert.NotEqual(first.Items[0].Key, second.Items[0].Key);
        Assert.Throws<InvalidOperationException>(() => query.Candidates(All with { Sort = "sender" }, MessageQueries.DecodeCursor(first.Next!), 1, default));
        MessageReadFixture.Mirror(a, b);
        Assert.Throws<InvalidOperationException>(() => query.Candidates(All, MessageQueries.DecodeCursor(first.Next!), 1, default));
    }
}

internal static class MessageReadFixture
{
    internal static void Seed(EngineFixture f, string id, long at, string text = "body", string? origin = null, string state = "queued", string? recipientMachine = null, string sender = "Sender")
    {
        var source = origin ?? f.Machine;
        if (f.Text("SELECT id FROM engine_session WHERE id='from'") is null) f.SeedSession("from");
        if (f.Text("SELECT id FROM engine_session WHERE id='to'") is null) f.SeedSession("to");
        f.Db.Writer.Write("message fixture", s =>
        {
            s.Execute("UPDATE engine_session SET machine=$source,state=CASE WHEN $source=$local THEN 'idle' ELSE 'foreign' END WHERE id='from'",
                new("$source", source), new("$local", f.Machine));
            s.Execute("UPDATE engine_session SET machine=$recipient,state=CASE WHEN $recipient=$local THEN 'idle' ELSE 'foreign' END WHERE id='to'",
                new("$recipient", recipientMachine ?? f.Machine), new("$local", f.Machine));
            s.Execute("""
                INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,from_session_id,sender_kind,to_session_id,text,mode,state,delivery,sender_label,origin_machine)
                VALUES($id,1,$machine,$at,$at,'from','session','to',$text,'prompt',$state,'wait',$sender,$origin)
                """, new("$id", id), new("$machine", f.Machine), new("$at", at), new("$text", text), new("$state", state), new("$sender", sender), new("$origin", source));
            return EngineJournal.Commit(s, new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "fixture"), id, 0, 0, at), 1, 0,
                [new("message.changed", "message", id, 1, false, new("to", null, null, "from", state, 0)) { MessageCreated = true }], []);
        });
    }

    internal static void Mirror(EngineFixture target, EngineFixture source, bool omitDelivery = false)
    {
        target.Db.Writer.Write("mirror.open", s => MirrorApply.Open(s, source.Machine, "route-" + source.Machine, 1000));
        target.Db.Writer.Write("mirror.begin", s => MirrorApply.BeginSnapshot(s, source.Machine, 1000));
        using var snapshot = new EngineReader(source.Db, source.Machine).Snapshot("mirror", null, default); SnapshotPage page;
        do
        {
            page = snapshot.ReadNext();
            var items = page.Items.Select(item =>
            {
                if (!omitDelivery || item.GetProperty("entityKind").GetString() != "message") return item;
                var old = System.Text.Json.Nodes.JsonNode.Parse(item.GetRawText())!.AsObject(); old.Remove("delivery");
                using var json = JsonDocument.Parse(old.ToJsonString()); return json.RootElement.Clone();
            }).ToArray();
            target.Db.Writer.Write("mirror.page", s => { MirrorApply.SnapshotPage(s, source.Machine, items); return 0; });
        } while (page.HasMore);
        target.Db.Writer.Write("mirror.end", s => MirrorApply.EndSnapshot(s, source.Machine, page.Through, page.At, 1000));
    }
}
