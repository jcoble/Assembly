using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Reading;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.SessionTitles;

public sealed class MachineStartAckTests
{
    private static (MachineStartAckArgs Query, CommandResult Result) Started(EngineFixture f, string title = "Title")
    {
        Assert.True(f.Apply(new RegisterForeignSession("source", "codex", "Parent"), "parent", actor: new(ActorKind.System, "machine:source")).Applied);
        var command = EngineCommands.PrepareSessionTitle(new StartSession("codex", null, "/folder", title, "parent", null, "https://task", ManagedCheckout: false));
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.System, "machine:source"), Guid.NewGuid().ToString(), 0, 0, 1000);
        var result = f.Db.Writer.Write("test.start", s => new EngineCommands(f.Machine, f.Limits).ApplyPreparedSessionTitle(s, context, command));
        return (new(context.CommandId, "source", context.EntityId, 0, 0, MachineStartCanonical.FromPrepared(command)), result);
    }
    private static MachineStartAck Probe(EngineFixture f, MachineStartAckArgs query)
    {
        var reader = new EngineReader(f.Db, f.Machine);
        var prepared = reader.PrepareMachineStartAck(query, default);
        return f.Db.Writer.Write("test.read_only_ack", scope => reader.ReadMachineStartAck(scope, prepared));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owning_Start_ACK_reopens_exact_original_receipt_effect_and_current_child_without_upload(bool large)
    {
        using var f = new EngineFixture();
        var (query, result) = Started(f, "T\0😀" + (large ? new string('界', 1500000) : "small") + "suffix\0");
        Assert.True(result.Applied);
        var events = f.Count("engine_event"); var receipts = f.Count("engine_command_receipt");
        var ack = Probe(f, query);
        Assert.Equal(MachineStartAckState.Accepted, ack.State); Assert.Equal(result, ack.Recorded);
        Assert.Equal(query.EntityId, ack.Child!.Id); Assert.Equal("parent", ack.Child.ParentId);
        f.Db.Writer.Write("test.rename", s => s.Execute("UPDATE engine_session SET title='renamed',version=version+1 WHERE id=$id", new SqlArg("$id", query.EntityId)));
        Assert.Equal(2, Probe(f, query).Child!.Version);
        f.Reopen(); Assert.Equal(result, Probe(f, query).Recorded);
        Assert.Equal(events, f.Count("engine_event")); Assert.Equal(receipts, f.Count("engine_command_receipt")); Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Fact]
    public void Refused_Start_ACK_retains_original_result_without_effect_or_epoch_reconstruction()
    {
        using var f = new EngineFixture(); var (query, result) = Started(f, "");
        Assert.False(result.Applied); Assert.Equal("bad_args", result.Error);
        f.Reopen(); var ack = Probe(f, query);
        Assert.Equal(MachineStartAckState.Refused, ack.State); Assert.Equal(result, ack.Recorded);
        Assert.Null(ack.Child); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Equal(MachineStartAckState.Conflict, Probe(f, query with { Epoch = 7 }).State);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("missing_effect")]
    [InlineData("result")]
    [InlineData("missing_receipt")]
    [InlineData("origin")]
    public void Corrupt_or_ambiguous_owning_evidence_is_Unknown_and_never_repaired(string damage)
    {
        using var f = new EngineFixture(); var (query, _) = Started(f);
        f.Db.Writer.Write("test.damage", s => s.Execute(damage switch
        {
            "payload" => "UPDATE engine_outbox SET payload_json=json_set(payload_json,'$.title','changed')",
            "missing_effect" => "DELETE FROM engine_outbox",
            "result" => "UPDATE engine_command_receipt SET result_json='null' WHERE command_id=$id",
            "origin" => "UPDATE engine_outbox SET event_seq=event_seq+1",
            _ => "DELETE FROM engine_command_receipt WHERE command_id=$id"
        }, damage is "result" or "missing_receipt" ? [new SqlArg("$id", query.CommandId.ToString("N"))] : []));
        var count = f.Count("engine_session"); var receipts = f.Count("engine_command_receipt"); var effects = f.Count("engine_outbox");
        Assert.Equal(MachineStartAckState.Unknown, Probe(f, query).State);
        Assert.Equal(count, f.Count("engine_session")); Assert.Equal(receipts, f.Count("engine_command_receipt")); Assert.Equal(effects, f.Count("engine_outbox"));
    }

    [Fact]
    public void Receipt_appearing_after_outside_snapshot_is_Unknown_in_final_read_only_writer()
    {
        using var f = new EngineFixture(); var (query, _) = Started(f);
        var absent = query with { CommandId = Guid.NewGuid(), EntityId = Guid.NewGuid().ToString() };
        var reader = new EngineReader(f.Db, f.Machine);
        var prepared = reader.PrepareMachineStartAck(absent, default);
        f.Db.Writer.Write("test.appeared", s => s.Execute("INSERT INTO engine_command_receipt(command_id,fingerprint,result_json) VALUES($id,'bad','null')", new SqlArg("$id", absent.CommandId.ToString("N"))));
        Assert.Equal(MachineStartAckState.Unknown, f.Db.Writer.Write("test.final", s => reader.ReadMachineStartAck(s, prepared)).State);
        Assert.Equal(MachineStartAckState.Missing, Probe(f, absent with { CommandId = Guid.NewGuid() }).State);
    }

    [Theory]
    [InlineData("{\"canonical\":\"x\",\"commandSha256\":\"x\"}")]
    [InlineData("{\"canonical\":\"x\",\"canonical\":\"y\"}")]
    [InlineData("{\"commandSha256\":\"x\",\"commandUtf8Bytes\":4,\"titleUtf8Bytes\":2,\"extra\":0}")]
    public void Start_ACK_rejects_mixed_duplicate_or_unknown_canonical_fields(string canonical)
    {
        var json = "{\"commandId\":\"" + Guid.NewGuid() + "\",\"fromMachine\":\"source\",\"entityId\":\"" + Guid.NewGuid() +
            "\",\"expectedVersion\":0,\"epoch\":0,\"canonical\":" + canonical + "}";
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize(json, Assembly.Infrastructure.Engine.EngineSocketJson.Default.MachineStartAckArgs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_effect_or_receipt_changed_after_capture_returns_Unknown_without_repair(bool receipt)
    {
        using var f = new EngineFixture(); var (query, _) = Started(f);
        var reader = new EngineReader(f.Db, f.Machine); var prepared = reader.PrepareMachineStartAck(query, default);
        f.Db.Writer.Write("test.replace", scope => scope.Execute(receipt
            ? "UPDATE engine_command_receipt SET fingerprint='changed' WHERE command_id=$id"
            : "UPDATE engine_outbox SET payload_json=json_set(payload_json,'$.title','replaced') WHERE id=$id",
            new SqlArg("$id", query.CommandId.ToString("N") + (receipt ? "" : "-0"))));
        Assert.Equal(MachineStartAckState.Unknown, f.Db.Writer.Write("test.final", scope => reader.ReadMachineStartAck(scope, prepared)).State);
        Assert.Equal(2, f.Count("engine_session")); Assert.Equal(1, f.Count("engine_outbox"));
    }

}
