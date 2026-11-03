using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Effects;

namespace Assembly.Engine.Messages;

/// <summary>The one validated immutable message/reservation tuple used for provider delivery and completion.</summary>
public sealed record MessageDelivery(DeliverMessage Intent, string? Label, long Epoch, string? FromSessionId, string? FromMachine = null)
{
    public static MessageDelivery? Read(IReadScope scope, Effect effect, bool allowDone = false)
    {
        var intent = effect.Payload.Deserialize(global::Assembly.Engine.EngineJson.Default.DeliverMessage);
        if (intent is null || intent.ToSessionId != effect.SessionId || intent.How is not ("prompt" or "steer")) return null;
        return scope.QueryFirst("""
            SELECT CAST(m.text AS BLOB),CAST(m.sender_label AS BLOB),COALESCE(m.client_id,m.id),m.framed,m.held_epoch,m.attachment_ids_json,
                s.epoch,m.state,m.in_flight,s.state,m.attempt,m.client_id,m.from_session_id,m.sender_kind,m.target_epoch,sender.machine
            FROM engine_message m JOIN engine_session s ON s.id=m.to_session_id
            LEFT JOIN engine_session sender ON sender.id=m.from_session_id
            JOIN engine_meta meta ON meta.singleton=1 AND s.machine=meta.machine_id AND m.machine=meta.machine_id
            WHERE m.id=$message AND m.to_session_id=$session AND s.holder='local'
            """, r =>
        {
            var framed = r.Long(3) == 1;
            var body = new UTF8Encoding(false, true).GetString(r.Blob(0));
            var label = new UTF8Encoding(false, true).GetString(r.Blob(1));
            var canonical = effect.Id == $"send/{intent.MessageId}/{r.Long(10)}";
            var legacy = r.Long(10) == 1 && effect.Id == "send/" + intent.MessageId && !r.IsNull(11) && r.Text(11) == effect.Id && intent.ClientId == effect.Id;
            if (!canonical && !legacy) return null;
            var expected = framed && !legacy ? MessageText.Prompt(label, body) : body;
            var refs = JsonSerializer.Deserialize(r.Text(5), global::Assembly.Engine.EngineJson.Default.IReadOnlyListString)!;
            if (intent.Text != expected || intent.SenderLabel != label || intent.ClientId != r.Text(2) ||
                (intent.Framed != false) != framed || (!framed && (intent.HeldEpoch != r.Long(6) ||
                    r.IsNull(4) || r.Long(4) != intent.HeldEpoch || !(intent.AttachmentIds ?? []).SequenceEqual(refs))) ||
                !allowDone && (r.Text(7) != "queued" || r.Long(8) != 1 || r.Text(9) == "ended")) return null;
            if (framed && !r.IsNull(14) && (r.IsNull(4) || r.Long(14) != r.Long(4) ||
                r.Long(14) != r.Long(6) || intent.HeldEpoch != r.Long(14))) return null;
            var from = framed && r.Text(13) == "session" ? r.IsNull(12) ? null : r.Text(12) : null;
            if (framed && r.Text(13) == "session" && from is null) return null;
            var fromMachine = from is null || r.IsNull(15) ? null : r.Text(15);
            return new MessageDelivery(intent, framed ? intent.SenderLabel : null, r.Long(6), from,
                string.IsNullOrWhiteSpace(fromMachine) ? null : fromMachine);
        }, new SqlArg("$message", intent.MessageId), new("$session", effect.SessionId));
    }

}
