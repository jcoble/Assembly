using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Engine;
using Assembly.Engine.Sessions;
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
}
