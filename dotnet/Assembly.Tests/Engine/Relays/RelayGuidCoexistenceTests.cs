using System.Text;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.Relays;

public sealed class RelayGuidCoexistenceTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Ordinary_same_context_Guid_receipts_cannot_poison_typed_TEXT_receipts(bool genericFirst, bool accepted)
    {
        using var target = new EngineFixture(); using var source = RelaySourceTests.Source(20, out var body, out var label, target.Machine);
        target.SeedSession("recipient");
        Assert.True(target.Apply(new RegisterForeignSession(source.Machine, "codex", "stub"), "sender",
            actor: new(ActorKind.System, "machine:" + source.Machine)).Applied);
        var descriptor = RelayDigests.Describe(new EngineReader(source.Db, source.Machine).RelaySource(new("message", target.Machine), default));
        var key = "relay-send/v1/" + descriptor.IdentitySha256 + "/root";
        var guid = RelayReceiptKeys.ContextGuid(key); var commands = new EngineCommands(target.Machine, target.Limits);
        var context = new CommandContext(guid, target.Machine, new(ActorKind.System, "observer"), accepted ? "recipient" : "missing", 0, 0, 1000);
        CommandResult Ordinary() => target.Db.Writer.Write("generic", s => commands.Apply(s, context, new ReportSessionTitle("ordinary")));
        CommandResult? generic = genericFirst ? Ordinary() : null;
        var prepared = PreparedRelaySend.Create(descriptor, Encoding.UTF8.GetBytes(body), Encoding.UTF8.GetBytes(label));
        var ack = target.Db.Writer.Write("typed", s => commands.ApplyRelaySend(s, new(descriptor, 0), prepared, 1000));
        Assert.True(ack.First!.Applied); Assert.True(ack.DestinationPresent); Assert.False(ack.Unknown);
        generic ??= Ordinary(); Assert.Equal(accepted, generic.Applied);
        Assert.Equal(generic, Ordinary());
        var wrong = target.Db.Writer.Write("wrong-generic-body", s => commands.Apply(s, context, new StopSession()));
        Assert.False(wrong.Applied); Assert.Equal("command_id_conflict", wrong.Error);
        var replay = target.Db.Writer.Write("typed-replay", s => commands.ApplyRelaySend(s, new(descriptor, 0), null, 2000));
        Assert.Equal(ack.First, replay.First); Assert.False(replay.Unknown); Assert.Equal(1, target.Count("engine_message"));
        Assert.NotNull(target.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$id", new Assembly.Application.Common.Database.SqlArg("$id", guid.ToString("N"))));
        Assert.NotNull(target.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$id", new Assembly.Application.Common.Database.SqlArg("$id", key)));
    }
}
