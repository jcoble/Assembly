using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

public static class MessageRelease
{
    public static IReadOnlyList<EventDraft> Next(IWriteScope s, CommandContext c, string sessionId, IList<EffectDraft> effects)
    {
        var expired = MessageExpiry.ExpireDue(s, c, sessionId);
        List<EventDraft> changed = [.. expired.Events];
        if (expired.More) return changed;
        var owner = s.QueryFirst("SELECT epoch,state FROM engine_session WHERE id=$id AND machine=$machine",
            r => new { Epoch = r.Long(0), State = r.Text(1) }, new SqlArg("$id", sessionId), new("$machine", c.Machine));
        if (owner is not null)
        {
            changed.AddRange(s.Query($"""
                UPDATE engine_message AS m SET state='failed',reason=$reason,version=version+1,updated_ms=$now
                WHERE to_session_id=$session AND framed=0 AND sender_kind='window' AND state IN ('queued','held')
                    AND in_flight=0 AND {MessageExpiry.ReservationFree} AND ($ended=1 OR held_epoch<>$epoch)
                RETURNING id,version,from_session_id
                """, r => new EventDraft("message.changed", "message", r.Text(0), r.Long(1), false,
                    new(sessionId, null, null, r.IsNull(2) ? null : r.Text(2), "failed", owner.Epoch)),
                new("$reason", owner.State == "ended" ? "recipient_ended" : "moved"), new("$now", c.NowMs),
                new("$session", sessionId), new("$ended", owner.State == "ended" ? 1 : 0), new("$epoch", owner.Epoch)));
        }
        var session = s.QueryFirst("""
            SELECT version,epoch,state,can_steer,run_id FROM engine_session
            WHERE id=$id AND machine=$machine AND state IN ('idle','running') AND cancel_requested=0
             AND NOT EXISTS(SELECT 1 FROM engine_message WHERE to_session_id=$id AND in_flight=1)
             AND NOT EXISTS(SELECT 1 FROM engine_needs_me WHERE session_id=$id AND state='open')
            """, r => new { Version = r.Long(0), Epoch = r.Long(1), State = r.Text(2), CanSteer = r.Long(3) != 0, Run = r.IsNull(4) ? null : r.Text(4) }, new SqlArg("$id", sessionId), new("$machine", c.Machine));
        if (session is null || session.State == "running" && !session.CanSteer)
            return changed;
        var message = s.QueryFirst($"""
            SELECT m.id,m.version,m.text,m.sender_label,COALESCE(m.client_id,m.id),m.attempt,m.from_session_id,m.framed,m.attachment_ids_json,m.held_epoch,CAST(m.text AS BLOB)
            FROM engine_message m JOIN engine_session se ON se.id=m.to_session_id
            WHERE m.to_session_id=$session AND m.state='queued' AND {MessageExpiry.Eligible} AND m.expires_ms>$now
             AND (m.framed=1 OR m.held_epoch=$epoch) AND ($idle=1 OR m.delivery='steer') ORDER BY m.created_ms,m.rowid LIMIT 1
            """, r => new { Id = r.Text(0), Version = r.Long(1), Text = r.Long(7) != 0 ? r.Text(2) : new System.Text.UTF8Encoding(false, true).GetString(r.Blob(10)), Label = r.Text(3), ClientId = r.Text(4), Attempt = r.Long(5), From = r.IsNull(6) ? null : r.Text(6), Framed = r.Long(7) != 0, Attachments = r.Text(8), HeldEpoch = r.IsNull(9) ? (long?)null : r.Long(9) }, new("$session", sessionId), new("$idle", session.State == "idle" ? 1 : 0), new("$epoch", session.Epoch), new("$machine", c.Machine), new("$now", c.NowMs));
        if (message is null)
            return changed;
        var reserved = s.Execute($"""
            UPDATE engine_message AS m SET version=version+1,in_flight=1,attempt=attempt+1,updated_ms=$now
            WHERE m.id=$id AND m.version=$version AND m.attempt=$attempt AND m.state='queued' AND m.expires_ms>$now
              AND EXISTS(SELECT 1 FROM engine_session se WHERE se.id=m.to_session_id AND se.epoch=$epoch AND {MessageExpiry.Eligible})
            """, new("$now", c.NowMs), new("$id", message.Id), new("$version", message.Version),
            new("$attempt", message.Attempt), new("$epoch", session.Epoch), new("$machine", c.Machine));
        if (reserved == 0) return changed;
        effects.Add(new($"send/{message.Id}/{message.Attempt + 1}", "send", sessionId, session.Run,
         JsonSerializer.SerializeToElement(new DeliverMessage(message.Id, sessionId, message.Framed ? MessageText.Prompt(message.Label, message.Text) : message.Text, message.Label, session.State == "idle" ? "prompt" : "steer", message.ClientId, session.Version, message.Framed ? null : false, message.Framed ? null : JsonSerializer.Deserialize(message.Attachments, EngineJson.Default.IReadOnlyListString), message.Framed ? null : message.HeldEpoch), EngineJson.Default.DeliverMessage)));
        changed.Add(new("message.changed", "message", message.Id, message.Version + 1, false, new(sessionId, null, null, message.From, "queued", session.Epoch)));
        return changed;
    }
}
