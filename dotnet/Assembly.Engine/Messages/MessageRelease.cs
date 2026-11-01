using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

public static class MessageRelease
{
    public static IReadOnlyList<EventDraft> Next(IWriteScope s, CommandContext c, string sessionId, IList<EffectDraft> effects)
    {
        var session = s.QueryFirst("""
            SELECT version,epoch,state,can_steer,run_id FROM engine_session
            WHERE id=$id AND state IN ('idle','running') AND cancel_requested=0
             AND NOT EXISTS(SELECT 1 FROM engine_message WHERE to_session_id=$id AND in_flight=1)
             AND NOT EXISTS(SELECT 1 FROM engine_needs_me WHERE session_id=$id AND state='open')
            """, r => new { Version = r.Long(0), Epoch = r.Long(1), State = r.Text(2), CanSteer = r.Long(3) != 0, Run = r.IsNull(4) ? null : r.Text(4) }, new SqlArg("$id", sessionId));
        if (session is null || session.State == "running" && !session.CanSteer)
            return [];
        var message = s.QueryFirst("""
            SELECT id,version,text,sender_label,COALESCE(client_id,id),attempt,from_session_id
            FROM engine_message WHERE to_session_id=$session AND state='queued' AND in_flight=0
             AND ($idle=1 OR delivery='steer') ORDER BY created_ms,rowid LIMIT 1
            """, r => new { Id = r.Text(0), Version = r.Long(1), Text = r.Text(2), Label = r.Text(3), ClientId = r.Text(4), Attempt = r.Long(5), From = r.IsNull(6) ? null : r.Text(6) }, new("$session", sessionId), new("$idle", session.State == "idle" ? 1 : 0));
        if (message is null)
            return [];
        s.Execute("UPDATE engine_message SET version=version+1,in_flight=1,attempt=attempt+1,updated_ms=$now WHERE id=$id", new("$now", c.NowMs), new("$id", message.Id));
        effects.Add(new($"send/{message.Id}/{message.Attempt + 1}", "send", sessionId, session.Run,
         JsonSerializer.SerializeToElement(new DeliverMessage(message.Id, sessionId, MessageText.Prompt(message.Label, message.Text), message.Label, session.State == "idle" ? "prompt" : "steer", message.ClientId, session.Version), EngineJson.Default.DeliverMessage)));
        return [new("message.changed", "message", message.Id, message.Version + 1, false, new(sessionId, null, null, message.From, "queued", session.Epoch))];
    }
}
