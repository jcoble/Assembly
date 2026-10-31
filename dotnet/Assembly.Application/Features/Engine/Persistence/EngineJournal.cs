using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;

namespace Assembly.Application.Features.Engine.Persistence;

public sealed record EventDraft(string Kind, string EntityKind, string EntityId,
    long Version, bool Late, EngineEventData Data);
public sealed record EffectDraft(string Id, string Kind, string? SessionId,
    string? RunId, JsonElement Payload);

/// <summary>Uses the caller's writer transaction for state, journal, effects and durable command receipts.</summary>
public static class EngineJournal
{
    public static CommandResult Execute(IWriteScope scope, CommandContext context,
        string canonicalCommandJson, Func<CommandResult> transition)
    {
        var fingerprint = Fingerprint(context, canonicalCommandJson);
        var receipt = scope.QueryFirst("SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$id",
            r => new { Fingerprint = r.Text(0), Result = r.Text(1) }, new SqlArg("$id", context.CommandId.ToString("N")));
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
            new("$id", context.CommandId.ToString("N")), new("$hash", fingerprint),
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
            scope.Execute("""
                INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,run_id,kind,payload_json,state,attempt)
                VALUES($id,0,$machine,$now,$now,$seq,$session,$run,$kind,$payload,'pending',0)
                """, new("$id", effect.Kind == "answer" ? effect.Id : $"{context.CommandId:N}-{index}"), new("$machine", context.Machine), new("$now", context.NowMs),
                new("$seq", last), new("$session", effect.SessionId), new("$run", effect.RunId),
                new("$kind", effect.Kind), new("$payload", effect.Payload.GetRawText()));
        }
        if (events.Count > 0 || effects.Count > 0)
            scope.Publish("__engine_committed", null, last, EngineJson.Default.Int64);
        return new(true, late, null, version, last, epoch);
    }

    private static CommandResult Rejected(CommandContext context, string error) =>
        new(false, false, error, context.ExpectedVersion, 0, context.Epoch);

    private static string Fingerprint(CommandContext context, string command)
    {
        // Serialize components independently to avoid delimiter ambiguity. Receipt time is intentionally absent.
        var identity = JsonSerializer.Serialize(context with { CommandId = Guid.Empty, NowMs = 0 }, EngineJson.Default.CommandContext);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + command)));
    }
}
