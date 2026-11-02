using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Messages;

public static class MessageTransitions
{
    private sealed record Message(string Id, long Version, string? From, string To, string Text, string Mode, string State);
    private sealed record Recipient(long Epoch, string State, bool CancelRequested, string? RunId);

    public static CommandResult Apply(IWriteScope s, CommandContext c, MessageCommand command, int messageLimit = 20, MessageExpiryPolicy? expiryPolicy = null)
    {
        if (string.IsNullOrWhiteSpace(c.EntityId))
            return Refused(c, "bad_args");
        var current = Find(s, c.EntityId);
        if (command is SendMessage send)
            return Send(s, c, send, current, messageLimit, expiryPolicy);
        if (current is null)
            return Refused(c, "bad_args");
        if (c.Actor.Kind != ActorKind.System)
            return Refused(c, "forbidden", current.Version);
        var recipient = FindRecipient(s, current.To)!;
        if (c.Epoch != recipient.Epoch)
            return Refused(c, "stale_epoch", current.Version, recipient.Epoch);
        if (s.QueryFirst("SELECT held_epoch FROM engine_message WHERE id=$id AND framed=0",
            r => r.IsNull(0) ? (long?)null : r.Long(0), new SqlArg("$id", current.Id)) is { } held && held != recipient.Epoch)
            return Refused(c, "stale_epoch", current.Version, recipient.Epoch);
        if (c.ExpectedVersion != current.Version)
            return Refused(c, "version_conflict", current.Version);
        if (command is not MessageOutcome outcome)
            return Refused(c, "unsupported_command", current.Version);
        if (outcome.State is not ("queued" or "delivered_to_turn" or "next_turn" or "unsupported" or "failed"))
            return Refused(c, "bad_args", current.Version);
        if (current.State == "held")
            return Refused(c, "unavailable", current.Version);
        if (current.State != "queued" || s.QueryFirst("SELECT in_flight FROM engine_message WHERE id=$id", r => r.Long(0), new SqlArg("$id", current.Id)) != 1)
            return Refused(c, "invalid_transition", current.Version);
        s.Execute("""
            UPDATE engine_message SET version=version+1,updated_ms=$now,in_flight=0,state=$state,provider_turn_id=$turn,reason=$reason WHERE id=$id
            """, new("$now", c.NowMs), new("$state", outcome.State), new("$turn", outcome.ProviderTurnId),
            new("$reason", outcome.Reason), new("$id", current.Id));
        var updated = current with
        {
            Version = current.Version + 1,
            State = outcome.State
        };
        List<EventDraft> events = [Changed(c, updated)];
        List<EffectDraft> effects = [];
        if (outcome.State == "next_turn")
        {
            var sessionVersion = s.QueryFirst("""
                UPDATE engine_session SET state='running',activity_state='running',version=version+1,updated_ms=$now
                WHERE id=$id RETURNING version
                """, r => r.Long(0), new("$now", c.NowMs), new("$id", current.To));
            events.Add(new("session.changed", "session", current.To, sessionVersion, false,
                new(current.To, recipient.RunId, null, null, "running", recipient.Epoch)));
        }
        var recipientVersion = s.QueryFirst("SELECT version FROM engine_session WHERE id=$id", r => r.Long(0), new SqlArg("$id", current.To));
        var releasedVersion = s.QueryFirst("""
            SELECT json_extract(payload_json,'$.recipientVersion') FROM engine_outbox
            WHERE kind='send' AND json_extract(payload_json,'$.messageId')=$id ORDER BY event_seq DESC,rowid DESC LIMIT 1
            """, r => r.Long(0), new SqlArg("$id", current.Id));
        if (outcome.State != "queued" || outcome.Reason == "not_delivered" || recipientVersion != releasedVersion)
            ReleaseNext(s, c, current.To, effects, events);
        return EngineJournal.Commit(s, c, Find(s, current.Id)!.Version, c.Epoch, events, effects);
    }

    internal static CommandResult ApplyOwnerPrompt(IWriteScope s, CommandContext c, PreparedOwnerPrompt prepared, MessageExpiryPolicy? expiryPolicy = null)
    {
        if (string.IsNullOrWhiteSpace(prepared.ToSessionId) || prepared.TargetEpoch < 0 ||
            prepared.ClientId.Length is < 1 or > 64 || prepared.ClientId.Any(ch =>
                !(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
            return Refused(c, "bad_args");
        var recipient = FindRecipient(s, prepared.ToSessionId);
        if (recipient is null) return Refused(c, "bad_args");
        if (prepared.TargetEpoch is { } target && target != recipient.Epoch)
            return Refused(c, "stale_epoch", epoch: recipient.Epoch);
        if (recipient.State == "ended") return Refused(c, "unavailable", epoch: recipient.Epoch);
        if (Find(s, c.EntityId) is not null) return Refused(c, "invalid_transition", epoch: recipient.Epoch);
        var captured = c with { Epoch = recipient.Epoch };
        var message = new Message(c.EntityId, 1, null, prepared.ToSessionId, prepared.Text, "prompt", "queued");
        Insert(s, captured, message, "window", null, expiryPolicy);
        s.Execute("""
            UPDATE engine_message SET text=CAST($rawText AS TEXT),framed=0,client_id=$client,attachment_ids_json=$attachments,
                held_epoch=$held,target_epoch=$target,owner_content_sha256=$digest WHERE id=$id
            """, new("$rawText", prepared.StorageTextUtf8), new("$client", prepared.ClientId), new("$attachments", prepared.AttachmentIdsJson),
            new("$held", recipient.Epoch), new("$target", prepared.TargetEpoch),
            new("$digest", prepared.ContentSha256), new("$id", c.EntityId));
        List<EventDraft> events = [Changed(captured, message)];
        List<EffectDraft> effects = [];
        ReleaseNext(s, captured, message.To, effects, events);
        return EngineJournal.Commit(s, captured, Find(s, message.Id)!.Version, recipient.Epoch, events, effects);
    }

    private static CommandResult Send(IWriteScope s, CommandContext c, SendMessage command, Message? current, int messageLimit, MessageExpiryPolicy? expiryPolicy)
    {
        if (c.Actor.Kind == ActorKind.Window ? command.FromSessionId is not null :
            c.Actor.Kind != ActorKind.Session || command.FromSessionId != c.Actor.Id)
            return Refused(c, "forbidden");
        if (command.Mode is not ("prompt" or "interrupt") || string.IsNullOrWhiteSpace(command.Text) ||
            string.IsNullOrWhiteSpace(command.ToSessionId))
            return Refused(c, "bad_args");
        var recipient = FindRecipient(s, command.ToSessionId);
        if (recipient is null || command.FromSessionId is not null && FindRecipient(s, command.FromSessionId) is null)
            return Refused(c, "bad_args");
        if (c.Epoch != recipient.Epoch)
            return Refused(c, "stale_epoch", current?.Version ?? 0, recipient.Epoch);
        if (c.ExpectedVersion != (current?.Version ?? 0))
            return Refused(c, "version_conflict", current?.Version ?? 0);
        if (current is not null)
            return Refused(c, "invalid_transition", current.Version);
        if (recipient.State == "ended")
            return Refused(c, "unavailable");
        if (command.FromSessionId is not null && s.QueryFirst("""
            SELECT COUNT(*) FROM engine_message WHERE from_session_id=$from AND to_session_id=$to
            """, r => r.Long(0), new("$from", command.FromSessionId), new("$to", command.ToSessionId)) >= messageLimit)
            return Refused(c, "message_limit");
        var message = new Message(c.EntityId, 1, command.FromSessionId, command.ToSessionId, command.Text,
            command.Mode, command.Mode == "interrupt" ? "held" : "queued");
        Insert(s, c, message, c.Actor.Kind == ActorKind.Window ? "window" : "session", null, expiryPolicy);
        List<EffectDraft> effects = [];
        List<EventDraft> events = [Changed(c, message)];
        if (message.State == "queued")
            ReleaseNext(s, c, message.To, effects, events);
        if (message.State == "held" && !recipient.CancelRequested)
        {
            var version = s.QueryFirst("""
                UPDATE engine_session SET cancel_requested=1,version=version+1,updated_ms=$now WHERE id=$id RETURNING version
                """, r => r.Long(0), new("$now", c.NowMs), new("$id", message.To));
            events.Add(new("session.cancel_requested", "session", message.To, version, false,
                new(message.To, recipient.RunId, null, null, recipient.State, recipient.Epoch)));
            effects.Add(new($"message-stop/{message.Id}", "stop", message.To, recipient.RunId,
                JsonSerializer.SerializeToElement(new Sessions.StopSession(), EngineJson.Default.StopSession)));
        }
        return EngineJournal.Commit(s, c, Find(s, message.Id)!.Version, c.Epoch, events, effects);
    }

    public static IReadOnlyList<EventDraft> ChildEnded(IWriteScope s, CommandContext c, string childId, IList<EffectDraft> effects, MessageExpiryPolicy? expiryPolicy = null)
    {
        var parent = s.QueryFirst("SELECT parent_id FROM engine_session WHERE id=$id",
            r => r.IsNull(0) ? null : r.Text(0), new SqlArg("$id", childId));
        if (parent is null || Find(s, $"child-ended/{childId}") is not null)
            return [];
        var ended = FindRecipient(s, parent)!.State == "ended";
        var message = new Message($"child-ended/{childId}", 1, childId, parent,
            $"Child session {childId} ended.", "prompt", ended ? "failed" : "queued");
        Insert(s, c, message, "system", ended ? "starter_ended" : null, expiryPolicy);
        List<EventDraft> events = [Changed(c, message)];
        if (!ended)
            ReleaseNext(s, c, message.To, effects, events);
        return events;
    }

    public static IReadOnlyList<EventDraft> NeedResolved(IWriteScope s, CommandContext c, string needId,
        string sessionId, string state, JsonElement? answer, MessageExpiryPolicy? expiryPolicy = null)
    {
        var id = $"need-answer/{needId}";
        if (FindRecipient(s, sessionId)?.State == "ended" || Find(s, id) is not null)
            return [];
        var text = state switch
        {
            "answered" => $"Jesse answered your question {needId}: {(answer!.Value.ValueKind == JsonValueKind.String ? answer.Value.GetString() : JsonSerializer.Serialize(answer.Value, EngineJson.Default.JsonElement))}",
            "dismissed" => $"Jesse dismissed your question {needId}.",
            _ => $"Your question {needId} closed without an answer ({state})."
        };
        var message = new Message(id, 1, null, sessionId, text, "prompt", "queued");
        Insert(s, c, message, "system", null, expiryPolicy);
        // NeedTransitions releases only after restoring session activity, using its current version.
        return [Changed(c, message)];
    }

    public static IReadOnlyList<EventDraft> ReleaseHeld(IWriteScope s, CommandContext c, string sessionId,
        bool ended, IList<EffectDraft> effects)
    {
        var expired = MessageExpiry.ExpireDue(s, c, sessionId);
        if (expired.More) return expired.Events;
        var messages = s.Query($"""
            UPDATE engine_message AS m SET state=$state,reason=$reason,version=version+1,updated_ms=$now
            WHERE to_session_id=$session AND state='held' AND in_flight=0 AND {MessageExpiry.ReservationFree}
            RETURNING id,version,from_session_id,to_session_id,CASE WHEN framed=0 THEN '' ELSE text END,mode,state
            """, ReadMessage, new("$state", ended ? "failed" : "queued"), new("$reason", ended ? "recipient_ended" : null),
            new("$now", c.NowMs), new("$session", sessionId));
        List<EventDraft> events = [.. expired.Events, .. messages.Select(message => Changed(c, message))];
        ReleaseNext(s, c, sessionId, effects, events);
        return events;
    }

    private static void ReleaseNext(IWriteScope s, CommandContext c, string sessionId,
        IList<EffectDraft> effects, List<EventDraft> events)
    {
        foreach (var released in MessageRelease.Next(s, c, sessionId, effects))
        {
            // Publish the final revision when this transaction also inserted or requeued the selected message.
            events.RemoveAll(e => e.EntityKind == "message" && e.EntityId == released.EntityId);
            events.Add(released);
        }
    }

    private static void Insert(IWriteScope s, CommandContext c, Message message, string senderKind, string? reason, MessageExpiryPolicy? expiryPolicy)
    {
        long? expiresMs = message.State is "queued" or "held" ? (expiryPolicy ?? new()).Deadline(s, c.NowMs) : null;
        s.Execute("""
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,from_session_id,sender_kind,to_session_id,text,mode,state,reason,expires_ms)
            VALUES($id,1,$machine,$now,$now,$from,$sender,$to,$text,$mode,$state,$reason,$expires)
            """, new("$id", message.Id), new("$machine", c.Machine), new("$now", c.NowMs), new("$from", message.From),
            new("$sender", senderKind), new("$to", message.To), new("$text", message.Text), new("$mode", message.Mode),
            new("$state", message.State), new("$reason", reason), new("$expires", expiresMs));
        var title = message.From is null ? null : s.QueryFirst("SELECT title FROM engine_session WHERE id=$id", r => r.Text(0), new SqlArg("$id", message.From));
        s.Execute("UPDATE engine_message SET delivery=$delivery,sender_label=$label WHERE id=$id",
            new("$delivery", DeliveryRule.For(s, message.From, senderKind, message.To)),
            new("$label", MessageText.Label(title, message.From, senderKind)), new("$id", message.Id));
    }

    private static EventDraft Changed(CommandContext c, Message message) => new("message.changed", "message", message.Id,
        message.Version, false, new(message.To, null, null, message.From, message.State, c.Epoch));
    private static Recipient? FindRecipient(IWriteScope s, string id) => s.QueryFirst(
        "SELECT epoch,state,cancel_requested,run_id FROM engine_session WHERE id=$id",
        r => new Recipient(r.Long(0), r.Text(1), r.Long(2) != 0, r.IsNull(3) ? null : r.Text(3)), new SqlArg("$id", id));
    private static Message? Find(IWriteScope s, string id) => s.QueryFirst(
        "SELECT id,version,from_session_id,to_session_id,CASE WHEN framed=0 THEN '' ELSE text END,mode,state FROM engine_message WHERE id=$id", ReadMessage, new SqlArg("$id", id));
    private static Message ReadMessage(IRow r) => new(r.Text(0), r.Long(1), r.IsNull(2) ? null : r.Text(2), r.Text(3), r.Text(4), r.Text(5), r.Text(6));
    private static CommandResult Refused(CommandContext c, string error, long version = 0, long? epoch = null) =>
        new(false, false, error, version, 0, epoch ?? c.Epoch);
}
