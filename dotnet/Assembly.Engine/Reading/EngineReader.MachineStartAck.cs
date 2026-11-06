using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Relays;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Reading;

public sealed partial class EngineReader
{
    internal PreparedMachineStartAck PrepareMachineStartAck(MachineStartAckArgs args, CancellationToken ct)
    {
        using var scope = db.OpenSnapshot(ct);
        var prepared = PreparedMachineStartAck.Capture(scope, machine, args);
        ct.ThrowIfCancellationRequested(); return prepared;
    }
    internal MachineStartAck ReadMachineStartAck(IReadScope scope, PreparedMachineStartAck prepared) => prepared.ReadCurrent(scope, machine);

    /// <summary>Exact outside-writer owning evidence; final admission performs only fixed SELECTs.</summary>
    internal sealed class PreparedMachineStartAck
    {
        private readonly string _machine;
        private readonly MachineStartAckArgs _args;
        private readonly Receipt? _receipt;
        private readonly OriginalEffect? _effect;
        private readonly StartSession? _original;
        private readonly MachineStartAckState _state;
        private readonly CommandResult? _recorded;
        private PreparedMachineStartAck(string target, MachineStartAckArgs args, Receipt? receipt, OriginalEffect? effect,
            StartSession? original, MachineStartAckState state, CommandResult? recorded)
        { _machine = target; _args = args; _receipt = receipt; _effect = effect; _original = original; _state = state; _recorded = recorded; }

        internal static PreparedMachineStartAck Capture(IReadScope scope, string target, MachineStartAckArgs args)
        {
            if (args.CommandId == Guid.Empty || !Guid.TryParse(args.EntityId, out _) || string.IsNullOrWhiteSpace(args.FromMachine) ||
                args.FromMachine == target || args.ExpectedVersion < 0 || args.Epoch < 0 || args.Canonical is null)
                throw new ArgumentException("bad_args");
            var canonical = MachineStartCanonical.Json(args.Canonical);
            var context = new CommandContext(Guid.Empty, target, new(ActorKind.System, "machine:" + args.FromMachine), args.EntityId,
                args.ExpectedVersion, args.Epoch, 0);
            var identity = JsonSerializer.Serialize(context, EngineJson.Default.CommandContext);
            // Closed read-only Start family. This never supplies a fingerprint to Execute or mutates a receipt.
            var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + canonical)));
            var receipt = ReadReceipt(scope, args.CommandId); var effect = ReadEffect(scope, args.CommandId);
            var child = ReadChild(scope, args.EntityId);
            PreparedMachineStartAck Result(MachineStartAckState state, CommandResult? recorded = null, StartSession? original = null) =>
                new(target, args, receipt, effect, original, state, recorded);
            if (scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != target)
                return Result(MachineStartAckState.Unknown);
            if (receipt is null) return Result(child is null && effect is null ? MachineStartAckState.Missing : MachineStartAckState.Unknown);
            if (receipt.Fingerprint != expected) return Result(MachineStartAckState.Conflict);
            var recorded = ParseResult(receipt.Result);
            if (recorded is null) return Result(MachineStartAckState.Unknown);
            if (!recorded.Applied)
                return Result(!recorded.Late && !string.IsNullOrWhiteSpace(recorded.Error) && recorded.Version >= 0 && recorded.Seq == 0 && recorded.Epoch >= 0 && effect is null
                    ? MachineStartAckState.Refused : MachineStartAckState.Unknown, recorded);
            if (args.ExpectedVersion != 0 || args.Epoch != 0 || recorded is not { Late: false, Error: null, Version: 1, Epoch: 0 } || recorded.Seq <= 0 ||
                effect is null || effect.Payload is null || effect.Machine != target || effect.Session != args.EntityId || effect.Kind != "start" || effect.EventSeq != recorded.Seq)
                return Result(MachineStartAckState.Unknown, recorded);
            try
            {
                var original = JsonSerializer.Deserialize(RelayHash.Utf8.GetString(effect.Payload), EngineJson.Default.StartSession);
                if (original is null || original.RunId != effect.Run) return Result(MachineStartAckState.Unknown, recorded);
                var prepared = PreparedSessionTitleCommand.Prepare(original);
                var frozen = (StartSession)prepared.Command;
                if (prepared.CanonicalJournal != canonical ||
                    !effect.Payload.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(prepared.StartEffectPayload!.ForExact(frozen).GetRawText())) ||
                    !ChildMatches(child, target, frozen)) return Result(MachineStartAckState.Unknown, recorded);
                return Result(MachineStartAckState.Accepted, recorded, frozen);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            { return Result(MachineStartAckState.Unknown, recorded); }
        }

        internal MachineStartAck ReadCurrent(IReadScope scope, string actualTarget)
        {
            if (actualTarget != _machine || scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0)) != _machine ||
                ReadReceipt(scope, _args.CommandId) != _receipt || !EffectMatches(scope)) return new(MachineStartAckState.Unknown, null, null);
            if (_state == MachineStartAckState.Missing)
                return new(ReadChild(scope, _args.EntityId) is null ? MachineStartAckState.Missing : MachineStartAckState.Unknown, null, null);
            if (_state != MachineStartAckState.Accepted) return new(_state, _recorded, null);
            var child = ReadChild(scope, _args.EntityId);
            return ChildMatches(child, _machine, _original!) ? new(MachineStartAckState.Accepted, _recorded, child)
                : new(MachineStartAckState.Unknown, _recorded, child);
        }

        private bool EffectMatches(IReadScope scope)
        {
            if (_effect is null) return scope.QueryFirst("SELECT COUNT(*) FROM engine_outbox WHERE id=$id", r => r.Long(0),
                new SqlArg("$id", _args.CommandId.ToString("N") + "-0")) == 0;
            if (_effect.Payload is null) return false;
            // SQL compares the frozen raw blob, without parsing/encoding/hashing full title bytes in this writer.
            return scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_outbox WHERE id=$id AND machine=$machine AND kind=$kind
                    AND session_id IS $session AND run_id IS $run AND event_seq=$seq AND CAST(payload_json AS BLOB)=$payload
                """, r => r.Long(0), new("$id", _args.CommandId.ToString("N") + "-0"), new("$machine", _effect.Machine), new("$kind", _effect.Kind),
                new("$session", _effect.Session), new("$run", _effect.Run), new("$seq", _effect.EventSeq), new("$payload", _effect.Payload)) == 1;
        }
        private static bool ChildMatches(OwningStartedChild? child, string target, StartSession original) =>
            child is not null && child.Machine == target && child.Version >= 1 && child.Epoch >= 0 && child.ReservedEpoch is not < 0 &&
            child.State != "foreign" && child.ParentId == original.ParentId && child.RunId == original.RunId;
        private static Receipt? ReadReceipt(IReadScope scope, Guid command) => scope.QueryFirst(
            "SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$id", r => new Receipt(r.Text(0), r.Text(1)), new SqlArg("$id", command.ToString("N")));
        private static OriginalEffect? ReadEffect(IReadScope scope, Guid command) => scope.QueryFirst("""
            SELECT machine,kind,session_id,run_id,event_seq,CAST(payload_json AS BLOB) FROM engine_outbox WHERE id=$id
            """, r => new OriginalEffect(r.Text(0), r.Text(1), r.IsNull(2) ? null : r.Text(2), r.IsNull(3) ? null : r.Text(3), r.Long(4), r.IsNull(5) ? null : r.Blob(5)),
            new SqlArg("$id", command.ToString("N") + "-0"));
        private static OwningStartedChild? ReadChild(IReadScope scope, string id) => scope.QueryFirst("""
            SELECT id,machine,holder,version,epoch,reserved_epoch,state,cancel_requested,parent_id,run_id FROM engine_session WHERE id=$id
            """, r => new OwningStartedChild(r.Text(0), r.Text(1), r.Text(2), r.Long(3), r.Long(4), r.IsNull(5) ? null : r.Long(5),
                r.Text(6), r.Long(7) != 0, r.IsNull(8) ? null : r.Text(8), r.IsNull(9) ? null : r.Text(9)), new SqlArg("$id", id));
        private static CommandResult? ParseResult(string json)
        {
            try
            {
                var result = JsonSerializer.Deserialize(json, EngineJson.Default.CommandResult);
                return result is not null && JsonSerializer.Serialize(result, EngineJson.Default.CommandResult) == json ? result : null;
            }
            catch (JsonException) { return null; }
        }
        private sealed record Receipt(string Fingerprint, string Result);
        private sealed record OriginalEffect(string Machine, string Kind, string? Session, string? Run, long EventSeq, byte[]? Payload);
    }
}
