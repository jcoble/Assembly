using Assembly.Engine;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine;

public sealed class HandoffAdmissionTests
{
    [Fact]
    public void Fixed_identity_distinguishes_actual_source_target_session_and_epoch_without_normalizing_text()
    {
        var identity = new HandoffIdentity("H", "A", "S", "B", 7, 8);
        var hash = HandoffAdmissionKeys.Identity(identity);
        Assert.Equal(64, hash.Length);
        Assert.All(hash, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        foreach (var other in new[] { identity with { HandoffId = "h" }, identity with { SourceMachine = "a" },
            identity with { SourceSessionId = "s" }, identity with { TargetMachine = "b" }, identity with { OldEpoch = 8, NextEpoch = 9 } })
            Assert.NotEqual(hash, HandoffAdmissionKeys.Identity(other));
        Assert.NotEqual(HandoffAdmissionKeys.Identity(identity with { SourceMachine = "a/b", SourceSessionId = "c" }),
            HandoffAdmissionKeys.Identity(identity with { SourceMachine = "a", SourceSessionId = "b/c" }));
    }

    [Fact]
    public void Target_addresses_require_only_actual_target_and_handoff_and_preserve_exact_identifiers()
    {
        Assert.Equal("handoff-target/v1/422fc3a9/482f58/admission", HandoffAdmissionKeys.TargetAdmission("B/é", "H/X"));
        Assert.Equal("handoff-target/v1/422fc3a9/482f58/closed", HandoffAdmissionKeys.TargetClosed("B/é", "H/X"));
        Assert.NotEqual(HandoffAdmissionKeys.TargetAdmission("B", "H"), HandoffAdmissionKeys.TargetAdmission("b", "H"));
        Assert.NotEqual(HandoffAdmissionKeys.TargetAdmission("B", "H"), HandoffAdmissionKeys.TargetAdmission("B", "h"));
    }

    [Fact]
    public void Phase_context_uses_fixed_original_phase_version_epoch_and_operation_key()
    {
        var identity = new HandoffIdentity("H", "A", "S", "B", 7, 8);
        var accept = HandoffAdmissionKeys.SourceContext(identity, HandoffPhase.Accept, false, 1000);
        Assert.Equal(("A", new EngineActor(ActorKind.System, "machine:B"), "H", 1L, 8L),
            (accept.Machine, accept.Actor, accept.EntityId, accept.ExpectedVersion, accept.Epoch));
        var terminal = HandoffAdmissionKeys.SourceContext(identity, HandoffPhase.Failed, true, 2000);
        Assert.Equal(2, terminal.ExpectedVersion);
        Assert.NotEqual(accept.CommandId, terminal.CommandId);
        Assert.Equal(terminal.CommandId, HandoffAdmissionKeys.SourceContext(identity, HandoffPhase.Resumed, true, 3000).CommandId);
        Assert.Equal(accept.CommandId, HandoffAdmissionKeys.SourceContext(identity, HandoffPhase.Accept, false, 9000).CommandId);
        Assert.DoesNotContain(accept.CommandId.ToString("N"), HandoffAdmissionKeys.SourceAccept(identity));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(7, 7)]
    [InlineData(7, 9)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void Invalid_epoch_bindings_are_rejected_before_private_addressing(long oldEpoch, long nextEpoch)
    {
        Assert.Throws<ArgumentException>(() => HandoffAdmissionKeys.Identity(new("H", "A", "S", "B", oldEpoch, nextEpoch)));
    }

    [Fact]
    public void Same_machine_and_invalid_utf8_do_not_mint_attempt_addresses()
    {
        Assert.Throws<ArgumentException>(() => HandoffAdmissionKeys.Identity(new("H", "A", "S", "A", 7, 8)));
        Assert.ThrowsAny<ArgumentException>(() => HandoffAdmissionKeys.TargetAdmission("B", "\ud800"));
    }

    [Fact]
    public void Sole_start_payload_preserves_every_ordinary_optional_field_and_exact_bytes()
    {
        foreach (var command in new[] {
            new Assembly.Engine.Sessions.StartSession("provider", null, "/folder", "title", null, null, null),
            new Assembly.Engine.Sessions.StartSession("provider", "model", "/folder", "title", "parent", null, null, "repo", true) })
        {
            var effect = SessionStartEffect.From(command);
            Assert.Equal(JsonSerializer.Serialize(command, EngineJson.Default.StartSession),
                JsonSerializer.Serialize(effect, EngineJson.Default.SessionStartEffect));
        }
    }

    [Fact]
    public void Pending_release_waits_without_allocating_an_immutable_phase_refusal()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7).Applied);
        var identity = new HandoffIdentity("H", f.Machine, "s", "target", 7, 8);
        var before = f.Count("engine_command_receipt");
        var first = f.Db.Writer.Write("test.phase-wait", scope => new EngineCommands(f.Machine, f.Limits)
            .ApplyHandoffPhase(scope, new(identity, HandoffPhase.Accept, 1, null, null), 1000));
        Assert.Equal(HandoffGate.Waiting, first.Gate); Assert.Null(first.Receipt);
        Assert.Equal("unavailable", first.Reason);
        Assert.Equal(before, f.Count("engine_command_receipt"));
        Assert.Equal("requested", f.State("engine_handoff", "H"));
    }

    [Fact]
    public void No_adopt_seal_is_one_immutable_target_slot_and_late_adopt_does_not_start()
    {
        using var f = new EngineFixture();
        var identity = new HandoffIdentity("H", "source", "S", f.Machine, 7, 8);
        var commands = new EngineCommands(f.Machine, f.Limits);
        var seal = f.Db.Writer.Write("test.seal", scope => commands.SealHandoffTarget(scope, new(identity, "missing"), 1000));
        Assert.Equal(HandoffTargetState.NotAdopted, seal.Target!.State);
        Assert.Equal("handoff_not_adopted", seal.Receipt!.Error);
        var bytes = f.Text("SELECT result_json FROM engine_command_receipt");
        var again = f.Db.Writer.Write("test.seal-repeat", scope => commands.SealHandoffTarget(scope, new(identity, "different diagnostic"), 2000));
        Assert.Equal(seal.Fingerprint, again.Fingerprint);
        Assert.Equal(bytes, f.Text("SELECT result_json FROM engine_command_receipt"));
        Assert.Equal(1, f.Count("engine_command_receipt"));
        var adopt = new Assembly.Engine.Sessions.AdoptSession("provider", null, "/folder", "title", "native", "source", "S", 8);
        var late = f.Db.Writer.Write("test.late-adopt", scope => commands.ApplyHandoffAdopt(scope,
            new(identity, adopt, new string('a', 64), new string('b', 64)), 3000));
        Assert.Equal(HandoffTargetState.NotAdopted, late.Target!.State);
        Assert.Equal(0, f.Count("engine_session")); Assert.Equal(0, f.Count("engine_outbox"));
    }

    [Fact]
    public void New_generic_outcome_is_refused_without_a_private_phase_allocation()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        Assert.True(f.Apply(new RequestHandoff("s", "target"), "H", epoch: 7).Applied);
        var identity = new HandoffIdentity("H", f.Machine, "s", "target", 7, 8);
        var context = HandoffAdmissionKeys.SourceContext(identity, HandoffPhase.Accept, false, 1000);
        var result = f.Db.Writer.Write("test.generic-outcome", scope => new EngineCommands(f.Machine, f.Limits)
            .Apply(scope, context, new HandoffOutcome("accepted", 8, null)));
        Assert.Equal("forbidden", result.Error);
        Assert.Null(f.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$key",
            new SqlArg("$key", HandoffAdmissionKeys.SourceAccept(identity))));
        Assert.NotNull(f.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$key",
            new SqlArg("$key", context.CommandId.ToString("N"))));
    }

    [Fact]
    public void Retained_start_binding_roundtrips_complete_original_identity_and_fingerprints()
    {
        var binding = new HandoffStartBinding(new("H", "A", "S", "B", 7, 8), "actual-account",
            new string('a', 64), new string('b', 64), new string('c', 64));
        var json = JsonSerializer.Serialize(binding, EngineJson.Default.HandoffStartBinding);
        Assert.Equal(binding, JsonSerializer.Deserialize(json, EngineJson.Default.HandoffStartBinding));
        using var payload = JsonDocument.Parse(json);
        Assert.Equal("actual-account", payload.RootElement.GetProperty("accountContext").GetString());
        Assert.Equal("S", payload.RootElement.GetProperty("identity").GetProperty("sourceSessionId").GetString());
        Assert.Equal(new string('c', 64), payload.RootElement.GetProperty("admissionFingerprint").GetString());
    }

    [Fact]
    public void Ordinary_guid_receipt_bytes_remain_immutable_even_when_time_and_current_state_change()
    {
        using var f = new EngineFixture(); f.SeedSession(epoch: 7);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), "H", 99, 7, 1000);
        var command = new RequestHandoff("s", "target");
        var first = f.Db.Writer.Write("test.original-refusal", scope => new EngineCommands(f.Machine, f.Limits).Apply(scope, context, command));
        Assert.Equal("version_conflict", first.Error);
        var bytes = f.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", context.CommandId.ToString("N")));
        f.Reopen();
        var replay = f.Db.Writer.Write("test.refusal-replay", scope => new EngineCommands(f.Machine, f.Limits).Apply(scope, context with { NowMs = 9000 }, command));
        Assert.Equal(first, replay);
        Assert.Equal(bytes, f.Text("SELECT result_json FROM engine_command_receipt WHERE command_id=$id", new SqlArg("$id", context.CommandId.ToString("N"))));
        var changed = f.Db.Writer.Write("test.changed-context", scope => new EngineCommands(f.Machine, f.Limits).Apply(scope, context with { ExpectedVersion = 0 }, command));
        Assert.Equal("command_id_conflict", changed.Error);
    }
}
