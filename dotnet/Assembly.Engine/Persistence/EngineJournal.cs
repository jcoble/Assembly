using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Persistence;

public sealed record EventDraft(string Kind, string EntityKind, string EntityId,
    long Version, bool Late, EngineEventData Data);
public sealed record EffectDraft(string Id, string Kind, string? SessionId,
    string? RunId, JsonElement Payload, string? OrderKey = null);

/// <summary>Uses the caller's writer transaction for state, journal, effects and durable command receipts.</summary>
public static partial class EngineJournal
{
    public static CommandResult Execute(IWriteScope scope, CommandContext context,
        string canonicalCommandJson, Func<CommandResult> transition)
        => ExecuteCore(scope, context, context.CommandId.ToString("N"), canonicalCommandJson, transition);

    private static CommandResult ExecuteCore(IWriteScope scope, CommandContext context, string receiptKey,
        string canonicalCommandJson, Func<CommandResult> transition, RelaySendAdmission? relayAdmission = null, TurnReplyAdmission? turnReplyAdmission = null, OwnerNeedAdmission? ownerNeedAdmission = null)
    {
        if ((relayAdmission is null ? 0 : 1) + (turnReplyAdmission is null ? 0 : 1) + (ownerNeedAdmission is null ? 0 : 1) > 1)
            throw new ArgumentException("Conflicting journal admissions.");
        relayAdmission?.ValidateBindings(scope, context, receiptKey, canonicalCommandJson);
        turnReplyAdmission?.ValidateBindings(scope, context, receiptKey, canonicalCommandJson);
        ownerNeedAdmission?.Validate(context, receiptKey, canonicalCommandJson);
        var fingerprint = ownerNeedAdmission?.OriginalFingerprint ?? Fingerprint(context, canonicalCommandJson);
        if (relayAdmission is not null) fingerprint = RelayFingerprint.Encode(context.Epoch, fingerprint);
        if (turnReplyAdmission is not null) fingerprint = turnReplyAdmission.Fingerprint(fingerprint);
        var receipt = scope.QueryFirst("SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$id",
            r => new { Fingerprint = r.Text(0), Result = r.Text(1) }, new SqlArg("$id", receiptKey));
        if (receipt is not null)
            return receipt.Fingerprint == fingerprint
                ? JsonSerializer.Deserialize(receipt.Result, EngineJson.Default.CommandResult)!
                : Rejected(context, "command_id_conflict");

        var machine = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0));
        var result = context.Machine != machine ? Rejected(context, "wrong_machine")
            : context.Actor is null || !Enum.IsDefined(context.Actor.Kind) || string.IsNullOrWhiteSpace(context.Actor.Id)
                ? Rejected(context, "invalid_actor")
            : Encoding.UTF8.GetByteCount(canonicalCommandJson) > 32 * 1024 ? Rejected(context, "command_too_large")
            : transition();
        scope.Execute("INSERT INTO engine_command_receipt(command_id,fingerprint,result_json) VALUES($id,$hash,$result)",
            new("$id", receiptKey), new("$hash", fingerprint),
            new("$result", JsonSerializer.Serialize(result, EngineJson.Default.CommandResult)));
        return result;
    }

    public static CommandResult Commit(IWriteScope scope, CommandContext context,
        long version, long epoch, IReadOnlyList<EventDraft> events,
        IReadOnlyList<EffectDraft> effects, bool late = false)
    {
        var next = scope.QueryFirst("SELECT next_seq FROM engine_meta WHERE singleton=1", r => r.Long(0));
        long last = 0;
        foreach (var draft in events)
        {
            var item = new EngineEvent(context.Machine, next, draft.Kind, draft.EntityKind, draft.EntityId,
                draft.Version, context.NowMs, draft.Late, draft.Data);
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item, EngineJson.Default.EngineEvent)) > 16 * 1024)
                throw new InvalidOperationException("Engine event exceeds the 16 KiB UTF-8 budget.");
            scope.Execute("""
                INSERT INTO engine_event(id,version,machine,created_ms,updated_ms,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
                VALUES($id,$version,$machine,$now,$now,$seq,$kind,$entityKind,$entityId,$version,$now,$late,$data)
                """, new("$id", $"{context.Machine}:{next}"), new("$version", draft.Version),
                new("$machine", context.Machine), new("$now", context.NowMs), new("$seq", next), new("$kind", draft.Kind),
                new("$entityKind", draft.EntityKind), new("$entityId", draft.EntityId), new("$late", draft.Late ? 1 : 0),
                new("$data", JsonSerializer.Serialize(draft.Data, EngineJson.Default.EngineEventData)));
            last = next;
            next = checked(next + 1);
        }
        if (events.Count > 0)
            scope.Execute("UPDATE engine_meta SET next_seq=$next WHERE singleton=1", new SqlArg("$next", next));
        for (var index = 0; index < effects.Count; index++)
        {
            var effect = effects[index];
            var id = (effect.Kind is "answer" or "send" or "probe_provider" or "begin_sign_in" or "cancel_sign_in" ||
                effect.Kind == "stop" && effect.Id.StartsWith("message-stop/", StringComparison.Ordinal))
                ? effect.Id : $"{context.CommandId:N}-{index}";
            scope.Execute("""
                INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,run_id,kind,payload_json,state,attempt,order_key)
                VALUES($id,0,$machine,$now,$now,$seq,$session,$run,$kind,$payload,'pending',0,$order)
                """, new("$id", id), new("$machine", context.Machine), new("$now", context.NowMs),
                new("$seq", last), new("$session", effect.SessionId), new("$run", effect.RunId),
                new("$kind", effect.Kind), new("$payload", effect.Payload.GetRawText()),
                new("$order", effect.OrderKey ?? effect.SessionId ?? ("effect/" + id)));
        }
        if (events.Count > 0 || effects.Count > 0)
            scope.Publish("__engine_committed", null, last, EngineJson.Default.Int64);
        return new(true, late, null, version, last, epoch);
    }

    private static CommandResult Rejected(CommandContext context, string error) =>
        new(false, false, error, context.ExpectedVersion, 0, context.Epoch);

    private static byte[] FingerprintPrefix(CommandContext context) => Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(context with { CommandId = Guid.Empty, NowMs = 0 }, EngineJson.Default.CommandContext) + "\n");

    private static string Fingerprint(CommandContext context, string command)
    {
        // Serialize components independently to avoid delimiter ambiguity. Receipt time is intentionally absent.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(FingerprintPrefix(context));
        hash.AppendData(Encoding.UTF8.GetBytes(command));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
