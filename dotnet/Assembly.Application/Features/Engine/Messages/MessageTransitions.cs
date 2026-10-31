using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;

namespace Assembly.Application.Features.Engine.Messages;

public static class MessageTransitions
{
    private sealed record Message(string Id, long Version, string? From, string To, string Text, string Mode, string State);
    private sealed record Recipient(long Epoch, string State, bool CancelRequested, string? RunId);

    public static CommandResult Apply(IWriteScope s, CommandContext c, MessageCommand command)
    {
        if (string.IsNullOrWhiteSpace(c.EntityId)) return Refused(c, "bad_args");
        var current = Find(s, c.EntityId);
        if (command is SendMessage send) return Send(s, c, send, current);
        if (current is null) return Refused(c, "bad_args");
        if (c.Actor.Kind != ActorKind.System) return Refused(c, "forbidden", current.Version);
        var recipient = FindRecipient(s, current.To)!;
        if (c.Epoch != recipient.Epoch) return Refused(c, "stale_epoch", current.Version, recipient.Epoch);
        if (c.ExpectedVersion != current.Version) return Refused(c, "version_conflict", current.Version);
        if (command is not MessageOutcome outcome) return Refused(c, "unsupported_command", current.Version);
        if (outcome.State is not ("delivered_to_turn" or "next_turn" or "unsupported" or "failed"))
            return Refused(c, "bad_args", current.Version);
        if (current.State == "held") return Refused(c, "unavailable", current.Version);
        if (current.State != "queued") return Refused(c, "invalid_transition", current.Version);
        s.Execute("""
            UPDATE engine_message SET version=version+1,updated_ms=$now,state=$state,provider_turn_id=$turn,reason=$reason WHERE id=$id
            """, new("$now", c.NowMs), new("$state", outcome.State), new("$turn", outcome.ProviderTurnId),
            new("$reason", outcome.Reason), new("$id", current.Id));
        var updated = current with { Version = current.Version + 1, State = outcome.State };
        return EngineJournal.Commit(s, c, updated.Version, c.Epoch, [Changed(c, updated)], []);
    }

    private static CommandResult Send(IWriteScope s, CommandContext c, SendMessage command, Message? current)
    {
        if (c.Actor.Kind == ActorKind.Window ? command.FromSessionId is not null :
            c.Actor.Kind != ActorKind.Session || command.FromSessionId != c.Actor.Id)
            return Refused(c, "forbidden");
        if (command.Mode is not ("prompt" or "interrupt") || string.IsNullOrWhiteSpace(command.Text) ||
            string.IsNullOrWhiteSpace(command.ToSessionId)) return Refused(c, "bad_args");
        var recipient = FindRecipient(s, command.ToSessionId);
        if (recipient is null || command.FromSessionId is not null && FindRecipient(s, command.FromSessionId) is null)
            return Refused(c, "bad_args");
        if (c.Epoch != recipient.Epoch) return Refused(c, "stale_epoch", current?.Version ?? 0, recipient.Epoch);
        if (c.ExpectedVersion != (current?.Version ?? 0)) return Refused(c, "version_conflict", current?.Version ?? 0);
        if (current is not null) return Refused(c, "invalid_transition", current.Version);
        if (recipient.State == "ended") return Refused(c, "unavailable");
        var message = new Message(c.EntityId, 1, command.FromSessionId, command.ToSessionId, command.Text,
            command.Mode, command.Mode == "interrupt" ? "held" : "queued");
        Insert(s, c, message, c.Actor.Kind == ActorKind.Window ? "window" : "session", null);
        List<EffectDraft> effects = [];
        List<EventDraft> events = [Changed(c, message)];
        if (message.State == "queued") effects.Add(SendEffect(message));
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
        return EngineJournal.Commit(s, c, 1, c.Epoch, events, effects);
    }

    public static EventDraft? ChildEnded(IWriteScope s, CommandContext c, string childId, IList<EffectDraft> effects)
    {
        var parent = s.QueryFirst("SELECT parent_id FROM engine_session WHERE id=$id",
            r => r.IsNull(0) ? null : r.Text(0), new SqlArg("$id", childId));
        if (parent is null || Find(s, $"child-ended/{childId}") is not null) return null;
        var ended = FindRecipient(s, parent)!.State == "ended";
        var message = new Message($"child-ended/{childId}", 1, childId, parent,
            $"Child session {childId} ended.", "prompt", ended ? "failed" : "queued");
        Insert(s, c, message, "system", ended ? "starter_ended" : null);
        if (!ended) effects.Add(SendEffect(message));
        return Changed(c, message);
    }

    public static IReadOnlyList<EventDraft> ReleaseHeld(IWriteScope s, CommandContext c, string sessionId,
        bool ended, IList<EffectDraft> effects)
    {
        var messages = s.Query("""
            UPDATE engine_message SET state=$state,reason=$reason,version=version+1,updated_ms=$now
            WHERE to_session_id=$session AND state='held'
            RETURNING id,version,from_session_id,to_session_id,text,mode,state
            """, ReadMessage, new("$state", ended ? "failed" : "queued"), new("$reason", ended ? "recipient_ended" : null),
            new("$now", c.NowMs), new("$session", sessionId));
        foreach (var message in messages)
            if (!ended) effects.Add(SendEffect(message));
        return messages.Select(message => Changed(c, message)).ToArray();
    }

    private static void Insert(IWriteScope s, CommandContext c, Message message, string senderKind, string? reason) =>
        s.Execute("""
            INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,from_session_id,sender_kind,to_session_id,text,mode,state,reason)
            VALUES($id,1,$machine,$now,$now,$from,$sender,$to,$text,$mode,$state,$reason)
            """, new("$id", message.Id), new("$machine", c.Machine), new("$now", c.NowMs), new("$from", message.From),
            new("$sender", senderKind), new("$to", message.To), new("$text", message.Text), new("$mode", message.Mode),
            new("$state", message.State), new("$reason", reason));

    private static EffectDraft SendEffect(Message message) => new($"send/{message.Id}", "send", message.To, null,
        JsonSerializer.SerializeToElement(new SendMessage(message.From, message.To, message.Text, message.Mode), EngineJson.Default.SendMessage));
    private static EventDraft Changed(CommandContext c, Message message) => new("message.changed", "message", message.Id,
        message.Version, false, new(message.To, null, null, message.From, message.State, c.Epoch));
    private static Recipient? FindRecipient(IWriteScope s, string id) => s.QueryFirst(
        "SELECT epoch,state,cancel_requested,run_id FROM engine_session WHERE id=$id",
        r => new Recipient(r.Long(0), r.Text(1), r.Long(2) != 0, r.IsNull(3) ? null : r.Text(3)), new SqlArg("$id", id));
    private static Message? Find(IWriteScope s, string id) => s.QueryFirst(
        "SELECT id,version,from_session_id,to_session_id,text,mode,state FROM engine_message WHERE id=$id", ReadMessage, new SqlArg("$id", id));
    private static Message ReadMessage(IRow r) => new(r.Text(0), r.Long(1), r.IsNull(2) ? null : r.Text(2), r.Text(3), r.Text(4), r.Text(5), r.Text(6));
    private static CommandResult Refused(CommandContext c, string error, long version = 0, long? epoch = null) =>
        new(false, false, error, version, 0, epoch ?? c.Epoch);
}
