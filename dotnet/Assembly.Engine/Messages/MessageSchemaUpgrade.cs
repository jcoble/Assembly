using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

/// <summary>Converts actual v1/machine-v2 send work inside Open's existing transaction, before any consumer exists.</summary>
internal static class MessageSchemaUpgrade
{
    public static void Apply(IWriteScope s, string machine, long nowMs)
    {
        if (s.QueryFirst("""
            SELECT COUNT(*) FROM engine_message m LEFT JOIN engine_outbox e ON e.id='send/'||m.id AND e.kind='send'
            WHERE m.state='queued' AND (e.id IS NULL OR e.state NOT IN ('pending','inflight','uncertain','unavailable','failed'))
            """, r => r.Long(0)) != 0)
            throw new InvalidOperationException("Inconsistent queued message/send history.");
        if (s.QueryFirst("""
            SELECT COUNT(*) FROM (SELECT m.to_session_id FROM engine_message m JOIN engine_outbox e ON e.id='send/'||m.id
             WHERE m.state='queued' AND e.state IN ('inflight','uncertain','unavailable') GROUP BY m.to_session_id HAVING COUNT(*)>1)
            """, r => r.Long(0)) != 0)
            throw new InvalidOperationException("Multiple unresolved sends for one recipient.");
        long cursor = 0;
        while (true)
        {
            var messages = s.Query("""
                SELECT m.rowid,m.id,m.from_session_id,m.sender_kind,CAST(se.title AS BLOB) FROM engine_message m
                LEFT JOIN engine_session se ON se.id=m.from_session_id
                WHERE m.rowid>$cursor AND m.state IN ('held','queued') ORDER BY m.rowid LIMIT 100
                """, r => new { Row = r.Long(0), Id = r.Text(1), From = r.IsNull(2) ? null : r.Text(2), Kind = r.Text(3), Title = r.IsNull(4) ? null : new System.Text.UTF8Encoding(false, true).GetString(r.Blob(4)) }, new SqlArg("$cursor", cursor));
            foreach (var m in messages)
            {
                s.Execute("UPDATE engine_message SET sender_label=CAST($label AS TEXT),client_id=CASE WHEN state='held' THEN id ELSE 'send/'||id END WHERE id=$id",
                 new("$label", new System.Text.UTF8Encoding(false, true).GetBytes(MessageText.Label(m.Title, m.From, m.Kind))), new("$id", m.Id));
                cursor = m.Row;
            }
            if (messages.Count < 100)
                break;
        }
        long seq = -1, rowid = 0;
        while (true)
        {
            var rows = s.Query("""
                SELECT e.rowid,e.event_seq,e.id,e.version,e.state,e.payload_json,e.last_error,
                 m.id,m.version,m.state,m.from_session_id,m.to_session_id,CAST(m.text AS BLOB),m.mode,CAST(m.sender_label AS BLOB),se.version,se.epoch
                FROM engine_outbox e JOIN engine_message m ON e.id='send/'||m.id
                 JOIN engine_session se ON se.id=m.to_session_id
                WHERE e.kind='send' AND (e.event_seq>$seq OR (e.event_seq=$seq AND e.rowid>$row))
                ORDER BY e.event_seq,e.rowid LIMIT 100
                """, r => new
            {
                Row = r.Long(0),
                Seq = r.Long(1),
                Id = r.Text(2),
                Version = r.Long(3),
                State = r.Text(4),
                Payload = r.Text(5),
                Error = r.IsNull(6) ? null : r.Text(6),
                MessageId = r.Text(7),
                MessageVersion = r.Long(8),
                MessageState = r.Text(9),
                From = r.IsNull(10) ? null : r.Text(10),
                To = r.Text(11),
                Text = new System.Text.UTF8Encoding(false, true).GetString(r.Blob(12)),
                Mode = r.Text(13),
                Label = new System.Text.UTF8Encoding(false, true).GetString(r.Blob(14)),
                RecipientVersion = r.Long(15),
                Epoch = r.Long(16)
            }, new("$seq", seq), new("$row", rowid));
            foreach (var e in rows)
            {
                seq = e.Seq;
                rowid = e.Row;
                if (e.MessageState is not ("queued" or "held"))
                {
                    if (e.State is not ("done" or "failed" or "dead"))
                        Retire(s, machine, nowMs, e.Id, e.Version, e.To, e.Epoch);
                    continue;
                }
                if (e.MessageState == "held")
                    throw new InvalidOperationException("Held message already has a send.");
                var old = JsonSerializer.Deserialize(e.Payload, EngineJson.Default.SendMessage)
                  ?? throw new InvalidOperationException("Missing old send payload.");
                if (old.FromSessionId != e.From || old.ToSessionId != e.To || old.Text != e.Text || old.Mode != e.Mode)
                    throw new InvalidOperationException("Old send does not match its message.");
                if (e.State == "pending")
                {
                    Retire(s, machine, nowMs, e.Id, e.Version, e.To, e.Epoch);
                    continue;
                }
                s.Execute("UPDATE engine_message SET attempt=1,in_flight=$flight WHERE id=$id", new("$flight", e.State == "failed" ? 0 : 1), new("$id", e.MessageId));
                if (e.State == "failed")
                {
                    s.Execute("UPDATE engine_message SET state='failed',reason=$reason,version=version+1,updated_ms=$now WHERE id=$id",
                     new("$reason", e.Error ?? "effect_failed"), new("$now", nowMs), new("$id", e.MessageId));
                    EngineJournal.Commit(s, Context(machine, nowMs, e.MessageId, e.MessageVersion, e.Epoch), e.MessageVersion + 1, e.Epoch,
                     [new("message.changed", "message", e.MessageId, e.MessageVersion + 1, false, new(e.To, null, null, e.From, "failed", e.Epoch))], []);
                }
                else
                {
                    var payload = new DeliverMessage(e.MessageId, e.To, e.Text, e.Label, "prompt", e.Id, e.RecipientVersion);
                    s.Execute("UPDATE engine_outbox SET payload_json=$payload WHERE id=$id", new("$payload", JsonSerializer.Serialize(payload, EngineJson.Default.DeliverMessage)), new("$id", e.Id));
                }
            }
            if (rows.Count < 100)
                break;
        }
    }
    private static CommandContext Context(string machine, long now, string id, long version, long epoch) => new(Guid.NewGuid(), machine, new(ActorKind.System, "engine.schema"), id, version, epoch, now);
    private static void Retire(IWriteScope s, string machine, long now, string id, long version, string recipient, long epoch)
    {
        s.Execute("UPDATE engine_outbox SET state='dead',version=version+1,updated_ms=$now WHERE id=$id", new("$now", now), new("$id", id));
        EngineJournal.Commit(s, Context(machine, now, id, version, epoch), version + 1, epoch,
         [new("effect.changed", "effect", id, version + 1, false, new(recipient, null, null, null, "dead", epoch))], []);
    }
}
