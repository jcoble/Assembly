using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Rows.Copies;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>
/// The saved copy of a remote session (TSK-1692): which machine it belongs to, how an existing install's forward copies
/// become saved copies, and what dropping one deletes.
/// </summary>
public sealed class RemoteCopyStoreTests : IDisposable
{
    private const string Box = "box-engine";
    private readonly EngineFixture _f = new();
    private readonly RowStore _rows;

    public RemoteCopyStoreTests()
    {
        _rows = RowStore.Open(_f.Db);
        Sessions(); // the app opens its session store, and so session_info, before adoption runs
    }

    // Pins: spec test 17 (store half)
    [Fact]
    public void Spec17_store_adopts_a_forward_copied_remote_session_as_downloading()
    {
        Mirror(Box, "workbox-test", generation: 3);
        MirrorRow(Box, "mirrored", generation: 3);
        Forward("mirrored", 3);
        // A child facade the follower made: session_info only, named by its route.
        Sessions().Add("facade", "codex", "/test", "workbox-test", parentSessionId: "mirrored");
        Forward("facade", 2);
        // A local session with rows is never a remote copy.
        _f.SeedSession("local");
        Forward("local", 2);

        Assert.Equal(2, _rows.Copies.AdoptRemoteCopies());

        Assert.Equal(new RemoteCopy("mirrored", Box, false, null), _rows.Copies.RemoteCopyOf("mirrored"));
        Assert.Equal(new RemoteCopy("facade", Box, false, null), _rows.Copies.RemoteCopyOf("facade"));
        Assert.Null(_rows.Copies.RemoteCopyOf("local"));
        Assert.Equal(3, _rows.LastSeq("mirrored"));
        Assert.Equal(1, _rows.Copies.FirstSeq("mirrored"));
        Assert.Equal(0, _rows.Copies.AdoptRemoteCopies());
    }

    [Fact]
    public void Adoption_skips_a_session_whose_engine_record_names_another_owner_and_one_without_rows()
    {
        Mirror(Box, "workbox-test", generation: 1);
        MirrorRow(Box, "contradicted", generation: 1);
        Forward("contradicted", 2);
        _f.SeedSession("contradicted"); // engine_session says this machine owns it
        MirrorRow(Box, "empty", generation: 1);

        Assert.Equal(0, _rows.Copies.AdoptRemoteCopies());
        Assert.Null(_rows.Copies.RemoteCopyOf("contradicted"));
        Assert.Null(_rows.Copies.RemoteCopyOf("empty"));
    }

    // Pins: spec test 13 (store half): the copy is keyed by the machine id alone.
    [Fact]
    public void Spec13_store_a_copy_keeps_its_owner_across_a_new_mirror_generation()
    {
        Mirror(Box, "workbox-test", generation: 1);
        MirrorRow(Box, "s1", generation: 1);
        Forward("s1", 2);
        _rows.Copies.AdoptRemoteCopies();

        _f.Db.Writer.Write("test.snapshot", scope => scope.Execute("""
            UPDATE engine_mirror SET generation=2 WHERE machine=$m;
            UPDATE engine_mirror_row SET generation=2 WHERE machine=$m;
            """, new SqlArg("$m", Box)));

        Assert.Equal(new RemoteCopy("s1", Box, false, null), _rows.Copies.RemoteCopyOf("s1"));
        Assert.Equal(["s1"], _rows.Copies.RemoteCopiesOn(Box).Select(copy => copy.Session));
    }

    [Fact]
    public void DropCopy_removes_rows_details_terminals_and_next_seq_but_not_session_info()
    {
        Mirror(Box, "workbox-test", generation: 1);
        Sessions().Add("s1", "codex", "/test", "workbox-test");
        Forward("s1", 3);
        _rows.Copies.AdoptRemoteCopies();
        _f.Db.Writer.Write("test.details", scope => scope.Execute("""
            INSERT INTO tool_details(session_id,item_id,output) VALUES('s1','t1','out');
            INSERT INTO tool_terminal_refs(session_id,item_id,acp_session_id,terminal_id,ordinal,current) VALUES('s1','t1','a','term',0,1);
            INSERT INTO acp_terminal_details(session_id,acp_session_id,terminal_id,output) VALUES('s1','a','term',x'00');
            """));

        _f.Db.Writer.Write("test.drop", scope => { CopyRecords.DropCopy(scope, "s1"); return 0; });

        foreach (var table in new[] { "rows", "tool_details", "tool_terminal_refs", "acp_terminal_details", "sessions", "remote_copy" })
            Assert.Equal(0, _f.Number($"SELECT COUNT(*) FROM {table} WHERE session_id='s1'"));
        Assert.Equal(1, _f.Number("SELECT COUNT(*) FROM session_info WHERE session_id='s1'"));
    }

    [Fact]
    public void MarkDownloading_records_the_remote_head()
    {
        Mirror(Box, "workbox-test", generation: 1);
        MirrorRow(Box, "s1", generation: 1);
        Forward("s1", 2);
        _rows.Copies.AdoptRemoteCopies();

        _f.Db.Writer.Write("test.mark", scope => { CopyRecords.MarkDownloading(scope, "s1", 9); return 0; });

        Assert.Equal(new RemoteCopy("s1", Box, false, 9), _rows.Copies.RemoteCopyOf("s1"));
    }

    // A child copied before cards named native sessions has none; its parent's newest card names it, and one set stays.
    [Fact]
    public void Adoption_fills_native_child_ids_from_the_parent_s_cards()
    {
        Mirror(Box, "workbox-test", generation: 1);
        MirrorRow(Box, "parent", generation: 1);
        Forward("parent", 2);
        Card("parent", 3, "child", "native-old");
        Card("parent", 4, "child", "native-new");
        Card("parent", 5, "named", "native-other");
        var sessions = Sessions();
        sessions.Add("child", "codex", "/test", "workbox-test", parentSessionId: "parent");
        sessions.Add("named", "codex", "/test", "workbox-test", parentSessionId: "parent");
        sessions.SetAcpSession("named", "native-kept");

        _rows.Copies.AdoptRemoteCopies();

        Assert.Equal("native-new", Sessions().Get("child")?.AcpSessionId);
        Assert.Equal("native-kept", Sessions().Get("named")?.AcpSessionId);
    }

    private void Card(string session, long seq, string child, string native) => Assert.True(_rows.Copy(new StoredRow(session, seq, "run",
        $"{CopyRecords.SubagentItem}{native}", "subagent", JsonSerializer.SerializeToElement(new { sessionId = child, title = child }),
        seq, PrevSeq: seq - 1)));

    private SessionStore Sessions() => SessionStore.Open(_f.Db, TimeProvider.System, "checkouts");

    private void Mirror(string machine, string name, long generation) => _f.Db.Writer.Write("test.mirror", scope => scope.Execute("""
        INSERT INTO engine_mirror(machine,name,generation,through,retained_after,state,updated_ms)
        VALUES($m,$name,$g,0,0,'live',0)
        """, new("$m", machine), new("$name", name), new("$g", generation)));

    private void MirrorRow(string machine, string session, long generation) => _f.Db.Writer.Write("test.mirror-row", scope => scope.Execute("""
        INSERT INTO engine_mirror_row(machine,entity_kind,entity_id,generation,version,row_json)
        VALUES($m,'session',$id,$g,0,json_object('id',$id,'machine',$m))
        """, new("$m", machine), new("$id", session), new("$g", generation)));

    // Rows the follower copied forward from seq 1, as an existing install holds them.
    private void Forward(string session, int count)
    {
        for (var seq = 1; seq <= count; seq++)
            Assert.True(_rows.Copy(new StoredRow(session, seq, "run", null, "turn",
                JsonSerializer.SerializeToElement(new { state = "finished" }), seq, PrevSeq: seq - 1)));
    }

    public void Dispose() => _f.Dispose();
}
