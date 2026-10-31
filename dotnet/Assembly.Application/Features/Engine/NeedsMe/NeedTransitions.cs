using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.Sessions;

namespace Assembly.Application.Features.Engine.NeedsMe;

public static class NeedTransitions
{
    private sealed record Need(string Id, long Version, string SessionId, string? RunId, string? StepId,
        string Kind, string Text, string Options, string? RequestId, string? ChatId, string State, long? Deadline);
    private sealed record Session(long Epoch, string State, string? RunId);

    public static CommandResult Apply(IWriteScope s, CommandContext c, NeedCommand command)
    {
        if (string.IsNullOrWhiteSpace(c.EntityId)) return Refused(c, "bad_args");
        if (command is OpenNeed open) return Open(s, c, open);
        var need = Find(s, c.EntityId);
        if (need is null) return Refused(c, "bad_args");
        var session = FindSession(s, need.SessionId)!;
        if (c.Epoch != session.Epoch) return Refused(c, "stale_epoch", need, session.Epoch);
        if (need.State != "open") return Refused(c, "already_resolved", need);
        if (c.ExpectedVersion != need.Version) return Refused(c, "version_conflict", need);
        return command is ResolveNeed resolve ? Resolve(s, c, need, resolve) : Refused(c, "unsupported_command", need);
    }

    public static CommandResult Expire(IWriteScope s, CommandContext c) => Apply(s, c, new ResolveNeed("expired", null));

    public static long? NextDeadline(IAppDatabase db) => db.QueryFirst(
        "SELECT MIN(deadline_ms) FROM engine_needs_me WHERE state='open' AND deadline_ms IS NOT NULL",
        r => r.IsNull(0) ? (long?)null : r.Long(0), default);

    private static CommandResult Open(IWriteScope s, CommandContext c, OpenNeed command)
    {
        if (command.Kind is not ("question" or "permission" or "choice" or "confirmation") ||
            string.IsNullOrWhiteSpace(command.Text) || command.Options is null ||
            command.Options.Any(string.IsNullOrWhiteSpace) || command.Options.Distinct().Count() != command.Options.Count ||
            command.Kind == "choice" && command.Options.Count < 2 ||
            command.ProviderRequestId is not null && string.IsNullOrWhiteSpace(command.ProviderRequestId) ||
            command.ChatRowId is not null && string.IsNullOrWhiteSpace(command.ChatRowId))
            return Refused(c, "bad_args");
        var session = FindSession(s, command.SessionId);
        if (session is null) return Refused(c, "bad_args");
        if (c.Epoch != session.Epoch) return Refused(c, "stale_epoch", epoch: session.Epoch);
        if (c.Actor.Kind != ActorKind.System && (c.Actor.Kind != ActorKind.Session || c.Actor.Id != command.SessionId))
            return Refused(c, "forbidden");
        var options = JsonSerializer.Serialize(command.Options, EngineJson.Default.IReadOnlyListString);
        var current = command.ProviderRequestId is null ? null : s.QueryFirst(
            "SELECT id FROM engine_needs_me WHERE session_id=$session AND provider_request_id=$request",
            r => r.Text(0), new("$session", command.SessionId), new("$request", command.ProviderRequestId));
        var existing = Find(s, current ?? c.EntityId);
        if (existing is not null)
        {
            var same = existing.SessionId == command.SessionId && existing.RunId == command.RunId &&
                existing.StepId == command.StepId && existing.Kind == command.Kind && existing.Text == command.Text &&
                existing.Options == options && existing.RequestId == command.ProviderRequestId &&
                existing.ChatId == command.ChatRowId && existing.Deadline == command.DeadlineMs;
            return same ? new(true, false, null, existing.Version, 0, session.Epoch)
                : Refused(c, "provider_request_conflict", existing);
        }
        if (c.ExpectedVersion != 0) return Refused(c, "version_conflict");
        if (session.State is not ("idle" or "running" or "needs_me")) return Refused(c, "invalid_transition");
        if (!ValidLinks(s, command, session)) return Refused(c, "bad_args");
        s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,run_id,step_id,
                kind,text,options_json,provider_request_id,chat_row_id,state,deadline_ms)
            VALUES($id,1,$machine,$now,$now,$session,$run,$step,$kind,$text,$options,$request,$chat,'open',$deadline)
            """, new("$id", c.EntityId), new("$machine", c.Machine), new("$now", c.NowMs), new("$session", command.SessionId),
            new("$run", command.RunId), new("$step", command.StepId), new("$kind", command.Kind), new("$text", command.Text),
            new("$options", options), new("$request", command.ProviderRequestId), new("$chat", command.ChatRowId), new("$deadline", command.DeadlineMs));
        var need = new Need(c.EntityId, 1, command.SessionId, command.RunId, command.StepId,
            command.Kind, command.Text, options, command.ProviderRequestId, command.ChatRowId, "open", command.DeadlineMs);
        List<EventDraft> events = [Changed(c, need, "needs_me.opened")];
        if (command.StepId is not null)
        {
            events.AddRange(s.Query("""
                UPDATE engine_step SET state='waiting',version=version+1,updated_ms=$now WHERE id=$id AND state='running'
                RETURNING version
                """, r => new EventDraft("step.changed", "step", command.StepId, r.Long(0), false,
                    new(command.SessionId, command.RunId, command.StepId, null, "waiting", c.Epoch)),
                new("$now", c.NowMs), new("$id", command.StepId)));
            s.Execute("""
                UPDATE engine_step_attempt SET state='waiting' WHERE step_id=$id AND state='running'
                  AND attempt=(SELECT attempt FROM engine_step WHERE id=$id)
                """, new SqlArg("$id", command.StepId));
        }
        return Commit(s, c, need, events, []);
    }

    private static bool ValidLinks(IWriteScope s, OpenNeed command, Session session)
    {
        if (command.StepId is not null && command.RunId is null) return false;
        if (command.RunId is not null)
        {
            var run = s.QueryFirst("SELECT owner_session_id,state FROM engine_run WHERE id=$id",
                r => new { Owner = r.Text(0), State = r.Text(1) }, new SqlArg("$id", command.RunId));
            if (run is null || RunTransitions.IsTerminal(run.State) || run.State == "cancelling" ||
                run.Owner != command.SessionId && session.RunId != command.RunId) return false;
        }
        return command.StepId is null || s.QueryFirst("""
            SELECT COUNT(*) FROM engine_step WHERE id=$step AND run_id=$run AND session_id=$session AND state IN ('running','waiting')
            """, r => r.Long(0), new("$step", command.StepId), new("$run", command.RunId), new("$session", command.SessionId)) == 1;
    }

    private static CommandResult Resolve(IWriteScope s, CommandContext c, Need need, ResolveNeed command)
    {
        if (command.State is not ("answered" or "dismissed" or "expired" or "cancelled")) return Refused(c, "bad_args", need);
        var human = command.State is "answered" or "dismissed";
        if (c.Actor.Kind != (human ? ActorKind.Window : ActorKind.System)) return Refused(c, "forbidden", need);
        if (command.State == "expired" && (need.Deadline is null || c.NowMs < need.Deadline))
            return Refused(c, "not_expired", need);
        if (command.State == "answered" && (command.Answer is null || command.Answer.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) ||
            command.State != "answered" && command.Answer is not null) return Refused(c, "bad_args", need);
        var answer = command.State == "answered" ? command.Answer : need.Kind == "permission"
            ? JsonSerializer.SerializeToElement("deny", EngineJson.Default.String) : (JsonElement?)null;
        s.Execute("""
            UPDATE engine_needs_me SET state=$state,version=version+1,updated_ms=$now,answer_json=$answer,answered_by=$actor,answered_ms=$now
            WHERE id=$id
            """, new("$state", command.State), new("$now", c.NowMs), new("$answer", answer?.GetRawText()),
            new("$actor", c.Actor.Id), new("$id", need.Id));
        var updated = need with { Version = need.Version + 1, State = command.State };
        List<EffectDraft> effects = [];
        if (answer is not null && need.RequestId is not null)
            effects.Add(new($"answer/{need.Id}", "answer", need.SessionId, need.RunId,
                JsonSerializer.SerializeToElement(new NeedAnswer(need.Id, need.RequestId, answer.Value), EngineJson.Default.NeedAnswer)));
        return Commit(s, c, updated, [Changed(c, updated, "needs_me.answered")], effects);
    }

    private static CommandResult Commit(IWriteScope s, CommandContext c, Need need, List<EventDraft> events, IReadOnlyList<EffectDraft> effects)
    {
        events.AddRange(SessionTransitions.RecomputeNeeds(s, c, need.SessionId));
        if (need.RunId is not null) events.AddRange(RunTransitions.Recompute(s, c, need.RunId));
        return EngineJournal.Commit(s, c, need.Version, c.Epoch, events, effects);
    }

    private static EventDraft Changed(CommandContext c, Need need, string kind) =>
        new(kind, "needs_me", need.Id, need.Version, false, new(need.SessionId, need.RunId, need.StepId, null, need.State, c.Epoch));
    private static Session? FindSession(IWriteScope s, string id) => s.QueryFirst(
        "SELECT epoch,state,run_id FROM engine_session WHERE id=$id",
        r => new Session(r.Long(0), r.Text(1), r.IsNull(2) ? null : r.Text(2)), new SqlArg("$id", id));
    private static Need? Find(IWriteScope s, string id) => s.QueryFirst("""
        SELECT id,version,session_id,run_id,step_id,kind,text,options_json,provider_request_id,chat_row_id,state,deadline_ms
        FROM engine_needs_me WHERE id=$id
        """, r => new Need(r.Text(0), r.Long(1), r.Text(2), r.IsNull(3) ? null : r.Text(3), r.IsNull(4) ? null : r.Text(4),
            r.Text(5), r.Text(6), r.Text(7), r.IsNull(8) ? null : r.Text(8), r.IsNull(9) ? null : r.Text(9),
            r.Text(10), r.IsNull(11) ? null : r.Long(11)), new SqlArg("$id", id));
    private static CommandResult Refused(CommandContext c, string error, Need? need = null, long? epoch = null) =>
        new(false, false, error, need?.Version ?? 0, 0, epoch ?? c.Epoch);
}
