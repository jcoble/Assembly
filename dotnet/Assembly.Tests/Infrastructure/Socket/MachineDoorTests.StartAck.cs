using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Sessions;
using Assembly.Engine.Relays;
using Assembly.Infrastructure.Engine.Relays;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class MachineDoorTests
{
    [Fact]
    public async Task Registered_Start_ACK_waits_behind_actual_original_start_writer_on_the_same_socket()
    {
        using var socket = await Open(); var parent = Guid.NewGuid().ToString(); var child = Guid.NewGuid().ToString();
        Assert.True(Result(await Machine(socket, new RegisterForeignSession("source", "custom", "Parent"), parent)).Applied);
        var commandId = Guid.NewGuid(); var start = new StartSession("custom", null, _root, "Title\0😀", parent, null, null, ManagedCheckout: false);
        var request = new MachineCommand(commandId, "source", child, 0, 0, start);
        var query = new MachineStartAckArgs(commandId, "source", child, 0, 0, MachineStartCanonical.FromPrepared(EngineCommands.PrepareSessionTitle(start)));
        _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously); _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Send(socket, "{\"t\":\"call\",\"id\":41,\"feature\":\"engine\",\"method\":\"machine_command\",\"args\":" + JsonSerializer.Serialize(request, EngineSocketJson.Default.MachineCommand) + "}");
        try
        {
            await _prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Send(socket, "{\"t\":\"call\",\"id\":42,\"feature\":\"engine\",\"method\":\"start_ack\",\"args\":" + JsonSerializer.Serialize(query, EngineSocketJson.Default.MachineStartAckArgs) + "}");
            var location = await Location(socket, parent); Assert.Equal(5, location.GetProperty("id").GetInt64());
            Assert.Equal(0, _f.Number("SELECT COUNT(*) FROM engine_session WHERE id=$id", new Assembly.Application.Common.Database.SqlArg("$id", child)));
            _continue.TrySetResult();
            var replies = new[] { await Read(socket), await Read(socket) }.ToDictionary(frame => frame.GetProperty("id").GetInt64());
            Assert.True(Result(replies[41]).Applied);
            var ack = replies[42].GetProperty("result").Deserialize(EngineSocketJson.Default.MachineStartAck)!;
            Assert.Equal(MachineStartAckState.Accepted, ack.State); Assert.True(ack.Recorded!.Applied); Assert.Equal(child, ack.Child!.Id);
            Assert.Equal(1, _f.Count("engine_outbox"));
        }
        finally { _continue.TrySetResult(); }
    }

    [Fact]
    public async Task Registered_ACK_missing_is_read_only_and_bulk_and_normal_enforce_owner_role()
    {
        var start = EngineCommands.PrepareSessionTitle(new StartSession("custom", null, _root, "Title", "parent", null, null));
        var query = new MachineStartAckArgs(Guid.NewGuid(), "source", Guid.NewGuid().ToString(), 0, 0, MachineStartCanonical.FromPrepared(start));
        using var normal = await Open();
        var frame = await RelayCall(normal, "start_ack", query, EngineSocketJson.Default.MachineStartAckArgs);
        Assert.Equal(MachineStartAckState.Missing, frame.GetProperty("result").Deserialize(EngineSocketJson.Default.MachineStartAck)!.State);
        using var bulk = await OpenBulk();
        var bulkFrame = await RelayCall(bulk, "start_ack_bulk", query, EngineSocketJson.Default.MachineStartAckArgs);
        Assert.Equal(MachineStartAckState.Missing, bulkFrame.GetProperty("result").Deserialize(EngineSocketJson.Default.MachineStartAck)!.State);
        foreach (var peer in new CallPeer?[] { null, new PhonePeer("phone") })
        {
            _overridePeer = true; _peer = peer;
            Assert.Equal("error", (await RelayCall(normal, "start_ack", query, EngineSocketJson.Default.MachineStartAckArgs)).GetProperty("t").GetString());
        }
        Assert.Equal(0, _f.Count("engine_session")); Assert.Equal(0, _f.Count("engine_command_receipt")); Assert.Equal(0, _f.Count("engine_outbox")); Assert.Empty(_f.Events());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Registered_Start_title_streams_preserve_full_bytes_typed_payload_and_zero_upload_replay(bool? managed, bool empty)
    {
        using var normal = await Open(); using var bulk = await OpenBulk();
        var parent = Guid.NewGuid().ToString();
        Assert.True(Result(await Ancestry(normal, new("source", parent, [new(parent, "source", "custom", "Parent", null)]))).Applied);
        var folder = managed == true ? (await Location(normal, parent)).GetProperty("result").GetProperty("folder").GetString()! : _root;
        var title = empty ? "" : new string('界', 1500000) + "\0AFTER_TITLE😀";
        var start = new StartSession("custom", null, folder, title, parent, null, null, managed == true ? "https://example.com/repo" : null, managed);
        var prepared = EngineCommands.PrepareSessionTitle(start); var facts = StartTitlePayload.Facts(start); var bytes = System.Text.Encoding.UTF8.GetBytes(title);
        var query = new MachineStartAckArgs(Guid.NewGuid(), "source", Guid.NewGuid().ToString(), 0, 0, MachineStartCanonical.FromPrepared(prepared));
        var request = new StartTitleBeginArgs(1, _runtime.Machine, query, new(facts.LongLength, RelayHash.Sha(facts)), new(bytes.LongLength, RelayHash.Sha(bytes)));
        var begin = (await RelayCall(bulk, "start_title_begin", request, EngineSocketJson.Default.StartTitleBeginArgs)).GetProperty("result")
            .Deserialize(EngineSocketJson.Default.StartTitleBeginReply)!;
        Assert.Equal(MachineStartAckState.Missing, begin.Evidence.State); Assert.NotNull(begin.Handle);
        foreach (var (part, data) in new[] { (StartTitlePartKind.Facts, facts), (StartTitlePartKind.Title, bytes) })
            for (var offset = 0; offset < data.Length; offset += RelaySegments.ChunkBytes)
                Assert.Equal("result", (await RelayCall(bulk, "start_title_part", new(begin.Handle!, part, offset,
                    data.AsSpan(offset, Math.Min(RelaySegments.ChunkBytes, data.Length - offset)).ToArray()), EngineSocketJson.Default.StartTitlePartArgs)).GetProperty("t").GetString());
        var result = Result(await RelayCall(bulk, "start_title_commit", new(request, begin.Handle), EngineSocketJson.Default.StartTitleCommitArgs));
        Assert.Equal(!empty, result.Applied);
        if (empty) { Assert.Equal("bad_args", result.Error); Assert.Equal(0, _f.Count("engine_outbox")); }
        else
        {
            Assert.Equal(bytes, _f.Db.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id", row => row.Blob(0), default,
                new Assembly.Application.Common.Database.SqlArg("$id", query.EntityId)));
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(start, EngineJson.Default.StartSession), _f.Db.QueryFirst(
                "SELECT CAST(payload_json AS BLOB) FROM engine_outbox WHERE id=$id", row => row.Blob(0), default,
                new Assembly.Application.Common.Database.SqlArg("$id", query.CommandId.ToString("N") + "-0")));
        }
        var replay = (await RelayCall(bulk, "start_title_begin", request, EngineSocketJson.Default.StartTitleBeginArgs)).GetProperty("result")
            .Deserialize(EngineSocketJson.Default.StartTitleBeginReply)!;
        Assert.Equal(empty ? MachineStartAckState.Refused : MachineStartAckState.Accepted, replay.Evidence.State);
        Assert.Equal(result, replay.Evidence.Recorded); Assert.Null(replay.Handle);
        Assert.Equal(result, Result(await RelayCall(bulk, "start_title_commit", new(request), EngineSocketJson.Default.StartTitleCommitArgs)));
        Assert.Equal(empty ? 0 : 1, _f.Count("engine_outbox"));
    }


    [Theory]
    [InlineData("facts")]
    [InlineData("utf8")]
    [InlineData("receipt_fault")]
    public async Task Registered_Start_title_rejects_changed_facts_bad_utf8_and_rolls_back_receipt_fault(string damage)
    {
        using var normal = await Open(); using var bulk = await OpenBulk(); var parent = Guid.NewGuid().ToString();
        Assert.True(Result(await Ancestry(normal, new("source", parent, [new(parent, "source", "custom", "Parent", null)]))).Applied);
        var start = new StartSession("custom", null, _root, "Title", parent, null, null);
        var prepared = EngineCommands.PrepareSessionTitle(start);
        var facts = StartTitlePayload.Facts(damage == "facts" ? start with { Model = "changed" } : start);
        var title = damage == "utf8" ? new byte[] { 255 } : System.Text.Encoding.UTF8.GetBytes(start.Title);
        var query = new MachineStartAckArgs(Guid.NewGuid(), "source", Guid.NewGuid().ToString(), 0, 0, MachineStartCanonical.FromPrepared(prepared));
        var request = new StartTitleBeginArgs(1, _runtime.Machine, query, new(facts.LongLength, RelayHash.Sha(facts)), new(title.LongLength, RelayHash.Sha(title)));
        var begin = (await RelayCall(bulk, "start_title_begin", request, EngineSocketJson.Default.StartTitleBeginArgs)).GetProperty("result")
            .Deserialize(EngineSocketJson.Default.StartTitleBeginReply)!;
        foreach (var (part, data) in new[] { (StartTitlePartKind.Facts, facts), (StartTitlePartKind.Title, title) })
            Assert.Equal("result", (await RelayCall(bulk, "start_title_part", new(begin.Handle!, part, 0, data), EngineSocketJson.Default.StartTitlePartArgs)).GetProperty("t").GetString());
        if (damage == "receipt_fault") _f.Db.Writer.Write("test.fault", scope => scope.Execute("CREATE TRIGGER start_receipt_fault BEFORE INSERT ON engine_command_receipt BEGIN SELECT RAISE(ABORT,'injected'); END"));
        var reply = await RelayCall(bulk, "start_title_commit", new(request, begin.Handle), EngineSocketJson.Default.StartTitleCommitArgs);
        Assert.Equal("error", reply.GetProperty("t").GetString());
        Assert.Equal(1, _f.Count("engine_session")); Assert.Equal(1, _f.Count("engine_command_receipt"));
        Assert.Equal(0, _f.Count("engine_outbox")); Assert.Empty(_f.Events()); Assert.Equal(0, _f.Number("SELECT COUNT(*) FROM session_info"));
    }

}
