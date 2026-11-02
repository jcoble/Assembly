using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class MachineDoorTests
{
    [Fact]
    public async Task Registered_full_foreign_title_uses_normal_receipt_and_zero_parts_replay()
    {
        using var source = new EngineFixture(); var id = Guid.NewGuid().ToString(); source.SeedSession(id);
        var title = "\0" + new string('界', 1500000) + "split😀suffix\0";
        source.Db.Writer.Write("test.title", s => s.Execute("UPDATE engine_session SET title=CAST($title AS TEXT)", new SqlArg("$title", Encoding.UTF8.GetBytes(title))));
        var capture = new EngineReader(source.Db, source.Machine).ForeignTitleSource(new(ForeignTitleSourceMode.NodeOnly, id, _runtime.Machine), default);
        var prepared = EngineCommands.PrepareSessionTitle(new RegisterForeignSession(source.Machine, "codex", title));
        var query = TitleQuery(Guid.NewGuid(), source.Machine, id, prepared.CanonicalJournal);
        var request = new ForeignTitleRequest(ForeignTitleOperation.RegisterForeign, _runtime.Machine, source.Machine, id,
            query, null, 1, capture.Manifest.NodeFacts, capture.Manifest.Titles);
        using var socket = await OpenBulk();
        var begin = await TitleBegin(socket, request); Assert.Equal(ForeignTitleReplyKind.PartsRequired, begin.Kind);
        await TitleAppend(socket, begin.Handle!, capture.NodeFacts, capture.Titles);
        var frame = await RelayCall(socket, "foreign_title_commit", new(request, begin.Handle), EngineSocketJson.Default.ForeignTitleCommitArgs);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        var committed = frame.GetProperty("result").Deserialize(EngineSocketJson.Default.ForeignTitleReply)!;
        Assert.Equal(ForeignTitleReplyKind.Applied, committed.Kind); Assert.True(committed.Result!.Applied);
        Assert.Null(committed.ObservedStoredMetadataSha256);
        Assert.Equal(Encoding.UTF8.GetBytes(title), _f.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id", r => r.Blob(0), default, new SqlArg("$id", id)));
        var context = new CommandContext(query.CommandId, _runtime.Machine, new(ActorKind.System, "machine:" + source.Machine), id, 0, 0, 0);
        var identity = JsonSerializer.Serialize(context with { CommandId = Guid.Empty }, EngineJson.Default.CommandContext);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + prepared.CanonicalJournal)));
        Assert.Equal(expected, _f.Text("SELECT fingerprint FROM engine_command_receipt WHERE command_id=$key", new SqlArg("$key", query.CommandId.ToString("N"))));
        var receipt = _f.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$key", new SqlArg("$key", query.CommandId.ToString("N")));
        var replay = await TitleBegin(socket, request);
        Assert.Equal(ForeignTitleReplyKind.RecordedCanonical, replay.Kind); Assert.Null(replay.Handle);
        Assert.Equal(receipt, JsonSerializer.Serialize(replay.Result, EngineJson.Default.CommandResult));
        Assert.NotNull(replay.ObservedStoredMetadataSha256);
        Assert.Equal(0, _f.Number("SELECT version+managed_checkout FROM engine_session WHERE id=$id", new SqlArg("$id", id)));
        Assert.Empty(_f.Events()); Assert.Equal(0, _f.Count("engine_outbox"));
    }

    [Fact]
    public async Task Registered_title_source_reads_full_parts_and_retains_snapshot_after_rename()
    {
        var id = Guid.NewGuid().ToString(); _f.SeedSession(id);
        var title = "\0title😀\0";
        _f.Db.Writer.Write("test.title", s => s.Execute("UPDATE engine_session SET title=CAST($title AS TEXT)", new SqlArg("$title", Encoding.UTF8.GetBytes(title))));
        using var socket = await OpenBulk();
        var frame = await RelayCall(socket, "foreign_title_source_open", new(ForeignTitleSourceMode.NodeOnly, id, "target"), EngineSocketJson.Default.ForeignTitleSourceOpenArgs);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        var opened = frame.GetProperty("result").Deserialize(EngineSocketJson.Default.ForeignTitleSourceOpened)!;
        _f.Db.Writer.Write("test.rename", s => s.Execute("UPDATE engine_session SET title='later'"));
        var partFrame = await RelayCall(socket, "foreign_title_source_part", new(opened.Handle, ForeignTitlePartKind.Titles, 0), EngineSocketJson.Default.ForeignTitlePartArgs);
        Assert.Equal(Encoding.UTF8.GetBytes(title), partFrame.GetProperty("result").Deserialize(EngineSocketJson.Default.ForeignTitlePart)!.Data);
        Assert.Equal("result", (await RelayCall(socket, "foreign_title_source_check", new(opened.Handle), EngineSocketJson.Default.RelayHandleArgs)).GetProperty("t").GetString());
        Assert.Equal("result", (await RelayCall(socket, "foreign_title_source_close", new(opened.Handle), EngineSocketJson.Default.RelayHandleArgs)).GetProperty("t").GetString());
    }

    private static ForeignRegisterReceiptQuery TitleQuery(Guid command, string source, string entity, string canonical)
    {
        using var json = JsonDocument.Parse(canonical); var root = json.RootElement;
        return new(command, source, entity, 0, 0, root.TryGetProperty("titleJournalVersion", out _)
            ? new(null, root.GetProperty("commandSha256").GetString(), root.GetProperty("commandUtf8Bytes").GetInt64(), root.GetProperty("titleUtf8Bytes").GetInt64())
            : new(canonical, null, null, null));
    }
    private static async Task<ForeignTitleReply> TitleBegin(WebSocket socket, ForeignTitleRequest request)
    {
        var frame = await RelayCall(socket, "foreign_title_begin", request, EngineSocketJson.Default.ForeignTitleRequest);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").Deserialize(EngineSocketJson.Default.ForeignTitleReply)!;
    }
    private static async Task TitleAppend(WebSocket socket, string handle, byte[] facts, byte[] titles)
    {
        foreach (var (kind, bytes) in new[] { (ForeignTitlePartKind.NodeFacts, facts), (ForeignTitlePartKind.Titles, titles) })
            for (var offset = 0; offset < bytes.Length; offset += 1048576)
            {
                var frame = await RelayCall(socket, "foreign_title_part", new(handle, kind, offset, bytes.AsSpan(offset, Math.Min(1048576, bytes.Length - offset)).ToArray()), EngineSocketJson.Default.ForeignTitleAppendArgs);
                Assert.Equal("result", frame.GetProperty("t").GetString());
            }
    }
}
