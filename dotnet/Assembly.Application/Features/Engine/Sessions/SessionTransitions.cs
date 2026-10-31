using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Runs;

namespace Assembly.Application.Features.Engine.Sessions;

public static class SessionTransitions
{
    private sealed record Session(long Version, long Epoch, string State, string Activity,
        string? ParentId, string? RunId);

    public static CommandResult Apply(IWriteScope scope, CommandContext context,
        SessionCommand command, int childLimit)
    {
        if (string.IsNullOrWhiteSpace(context.EntityId))
            return Refused(context, "bad_args");
        var current = Find(scope, context.EntityId);
        if (current is null && scope.QueryFirst("SELECT COUNT(*) FROM sessions WHERE session_id=$id",
                r => r.Long(0), new SqlArg("$id", context.EntityId)) != 0)
            return Refused(context, "legacy_session_not_managed");
        if (current is not null && current.Epoch != context.Epoch)
            return Refused(context, "stale_epoch", current);
        if ((current?.Version ?? 0) != context.ExpectedVersion)
            return Refused(context, "version_conflict", current);
        if (command is StartSession start)
            return current is null ? Start(scope, context, start, childLimit) : Refused(context, "invalid_transition", current);
        if (current is null)
            return Refused(context, "bad_args");
        return command switch
        {
            SetSessionState fact => SetState(scope, context, current, fact),
            StopSession => Stop(scope, context, current),
            _ => Refused(context, "unsupported_command", current)
        };
    }

    private static CommandResult Start(IWriteScope scope, CommandContext context, StartSession command, int childLimit)
    {
        if (string.IsNullOrWhiteSpace(command.Provider) || string.IsNullOrWhiteSpace(command.Model) ||
            string.IsNullOrWhiteSpace(command.Folder) || string.IsNullOrWhiteSpace(command.Title))
            return Refused(context, "bad_args");
        if (context.Actor.Kind != ActorKind.Window &&
            (context.Actor.Kind != ActorKind.Session || context.Actor.Id != command.ParentId))
            return Refused(context, "forbidden");
        Session? parent = null;
        if (command.ParentId is not null)
        {
            parent = Find(scope, command.ParentId);
            if (parent is null)
                return Refused(context, "bad_args");
            if (parent.ParentId is not null)
                return Refused(context, "ask your starter to start it");
            if (parent.State == "ended")
                return Refused(context, "invalid_transition");
        }
        if (command.RunId is not null)
        {
            var owner = scope.QueryFirst("SELECT owner_session_id FROM engine_run WHERE id=$id",
                r => r.Text(0), new SqlArg("$id", command.RunId));
            if (owner is null)
                return Refused(context, "bad_args");
            if (owner != command.ParentId)
                return Refused(context, "forbidden");
        }
        if (parent is not null && scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_session WHERE parent_id=$parent AND state != 'ended'
                """, r => r.Long(0), new SqlArg("$parent", command.ParentId)) >= childLimit)
            return Refused(context, "child_limit");
        if (context.Epoch != 0)
            return new(false, false, "stale_epoch", 0, 0, 0);
        scope.Execute("""
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,task_url,
                provider,model,folder,title,state,activity_state,parent_id,run_id,holder,epoch,cancel_requested)
            VALUES($id,1,$machine,$now,$now,$task,$provider,$model,$folder,$title,
                'starting','idle',$parent,$run,'local',0,0)
            """, new("$id", context.EntityId), new("$machine", context.Machine), new("$now", context.NowMs),
            new("$task", command.TaskUrl), new("$provider", command.Provider), new("$model", command.Model),
            new("$folder", command.Folder), new("$title", command.Title), new("$parent", command.ParentId), new("$run", command.RunId));
        var created = new Session(1, 0, "starting", "idle", command.ParentId, command.RunId);
        return Commit(scope, context, created, "session.started",
            [new("", "start", context.EntityId, command.RunId, JsonSerializer.SerializeToElement(command, EngineJson.Default.StartSession))]);
    }

    private static CommandResult SetState(IWriteScope scope, CommandContext context, Session current, SetSessionState command)
    {
        if (context.Actor.Kind != ActorKind.System)
            return Refused(context, "forbidden", current);
        if (string.IsNullOrWhiteSpace(command.State))
            return Refused(context, "bad_args", current);
        var from = current.State == "needs_me" ? current.Activity : current.State;
        var allowed = (from, command.State) is ("starting", "idle") or ("idle", "running") or ("running", "idle")
            or ("starting" or "idle" or "running", "lost" or "ended") or ("lost", "idle");
        if (!allowed)
            return Refused(context, "invalid_transition", current);
        var activity = command.State is "idle" or "running" ? command.State : current.Activity;
        var state = command.State;
        if (state is "idle" or "running" && scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_needs_me WHERE session_id=$id AND state='open'
                """, r => r.Long(0), new SqlArg("$id", context.EntityId)) > 0)
            state = "needs_me";
        var updated = current with { Version = current.Version + 1, State = state, Activity = activity };
        scope.Execute("""
            UPDATE engine_session SET version=$version,updated_ms=$now,state=$state,activity_state=$activity,
                provider_session_id=COALESCE($provider,provider_session_id),lost_ms=$lost WHERE id=$id
            """, new("$version", updated.Version), new("$now", context.NowMs), new("$state", state),
            new("$activity", activity), new("$provider", command.ProviderSessionId),
            new("$lost", command.State == "lost" ? context.NowMs : null), new("$id", context.EntityId));
        return Commit(scope, context, updated, "session.changed", []);
    }

    private static CommandResult Stop(IWriteScope scope, CommandContext context, Session current)
    {
        if (context.Actor.Kind != ActorKind.Window &&
            (context.Actor.Kind != ActorKind.Session || context.Actor.Id != context.EntityId))
            return Refused(context, "forbidden", current);
        if (current.State == "ended")
            return Refused(context, "invalid_transition", current);
        var updated = current with { Version = current.Version + 1 };
        scope.Execute("UPDATE engine_session SET cancel_requested=1,version=$version,updated_ms=$now WHERE id=$id",
            new("$version", updated.Version), new("$now", context.NowMs), new("$id", context.EntityId));
        return Commit(scope, context, updated, "session.cancel_requested",
            [new("", "stop", context.EntityId, current.RunId, JsonSerializer.SerializeToElement(new StopSession(), EngineJson.Default.StopSession))]);
    }

    private static CommandResult Commit(IWriteScope scope, CommandContext context, Session session,
        string kind, IReadOnlyList<EffectDraft> effects)
    {
        List<EventDraft> events = [new(kind, "session", context.EntityId, session.Version, false,
            new(context.EntityId, session.RunId, null, session.ParentId, session.State, session.Epoch))];
        var runs = scope.Query("""
            SELECT id FROM engine_run WHERE owner_session_id=$session
            UNION SELECT run_id FROM engine_step WHERE session_id=$session
            """, r => r.Text(0), new SqlArg("$session", context.EntityId));
        foreach (var run in runs) events.AddRange(RunTransitions.Recompute(scope, context, run));
        return EngineJournal.Commit(scope, context, session.Version, session.Epoch, events, effects);
    }

    private static Session? Find(IWriteScope scope, string id) => scope.QueryFirst("""
        SELECT version,epoch,state,activity_state,parent_id,run_id FROM engine_session WHERE id=$id
        """, r => new Session(r.Long(0), r.Long(1), r.Text(2), r.Text(3),
            r.IsNull(4) ? null : r.Text(4), r.IsNull(5) ? null : r.Text(5)), new SqlArg("$id", id));

    private static CommandResult Refused(CommandContext context, string error, Session? current = null) =>
        new(false, false, error, current?.Version ?? 0, 0, current?.Epoch ?? context.Epoch);
}
