using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Persistence;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class MachineDoorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registered_ancestry_transfers_single_or_aggregate_full_titles_and_replays_only_actual_structure(bool aggregate)
    {
        using var source = new EngineFixture(); var root = Guid.NewGuid().ToString(); var parent = Guid.NewGuid().ToString();
        source.SeedSession(root); source.SeedSession(parent, parentId: root);
        var first = "\0" + new string('x', aggregate ? 2300000 : 4300000) + "😀suffix\0";
        var last = aggregate ? new string('y', 2300000) + "P\0" : "";
        source.Db.Writer.Write("test.titles", s =>
        {
            s.Execute("UPDATE engine_session SET title=CAST($title AS TEXT) WHERE id=$id", new SqlArg("$title", Encoding.UTF8.GetBytes(first)), new SqlArg("$id", root));
            return s.Execute("UPDATE engine_session SET title=CAST($title AS TEXT) WHERE id=$id", new SqlArg("$title", Encoding.UTF8.GetBytes(last)), new SqlArg("$id", parent));
        });
        var capture = new EngineReader(source.Db, source.Machine).ForeignTitleSource(new(ForeignTitleSourceMode.OwnedAncestry, parent, _runtime.Machine), default);
        var proof = new ForeignAncestryProofQuery(source.Machine, parent, 2, ForeignTitleDigests.Structure(_runtime.Machine, source.Machine, parent, capture.Nodes));
        var request = new ForeignTitleRequest(ForeignTitleOperation.ForeignAncestry, _runtime.Machine, source.Machine, parent,
            null, proof, 2, capture.Manifest.NodeFacts, capture.Manifest.Titles);
        using var socket = await OpenBulk(); var begin = await TitleBegin(socket, request);
        Assert.Equal(ForeignTitleReplyKind.PartsRequired, begin.Kind);
        await TitleAppend(socket, begin.Handle!, capture.NodeFacts, capture.Titles);
        var frame = await RelayCall(socket, "foreign_title_commit", new(request, begin.Handle), EngineSocketJson.Default.ForeignTitleCommitArgs);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        var committed = frame.GetProperty("result").Deserialize(EngineSocketJson.Default.ForeignTitleReply)!;
        Assert.Equal(ForeignTitleReplyKind.Applied, committed.Kind); Assert.True(committed.Result!.Applied);
        foreach (var (id, title) in new[] { (root, first), (parent, last) })
            Assert.Equal(Encoding.UTF8.GetBytes(title), _f.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id", r => r.Blob(0), default, new SqlArg("$id", id)));
        Assert.Equal(root, _f.Text("SELECT parent_id FROM engine_session WHERE id=$id", new SqlArg("$id", parent)));
        var repeated = await TitleBegin(socket, request with { Titles = request.Titles with { Sha256 = new string('a', 64) } });
        Assert.Equal(ForeignTitleReplyKind.ExistingStructure, repeated.Kind); Assert.Null(repeated.Handle); Assert.Equal(committed.Result, repeated.Result);
        Assert.Equal(proof.StructureSha256, repeated.VerifiedStructureSha256);
        Assert.Equal(ForeignTitleDigests.Stored(_runtime.Machine, capture.Nodes), repeated.ObservedStoredMetadataSha256);
        Assert.Equal(2, _f.Count("engine_command_receipt")); Assert.Empty(_f.Events());
        _f.Db.Writer.Write("test.corrupt", s => s.Execute("UPDATE engine_command_receipt SET fingerprint='bad' WHERE command_id=$key", new SqlArg("$key", RelayReceiptKeys.ForeignParent(_runtime.Machine, parent))));
        var corrupt = await TitleBegin(socket, request); Assert.Equal(ForeignTitleReplyKind.Unknown, corrupt.Kind); Assert.Null(corrupt.Handle);
        Assert.Equal(2, _f.Count("engine_session")); Assert.Equal(0, _f.Count("engine_outbox"));
    }
}
