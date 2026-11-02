using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Messages;

namespace Assembly.Engine.Sessions;

public static partial class SessionTransitions
{
    private sealed record Session(long Version, long Epoch, string State, string Activity,
        string? ParentId, string? RunId, string Machine, bool CancelRequested = false, long? ReservedEpoch = null, string? ProjectId = null);

    public static CommandResult Apply(IWriteScope scope, CommandContext context,
        SessionCommand command, int depthLimit, int childLimit, string accountContext = "", MessageExpiryPolicy? expiryPolicy = null, ISessionProjectProjection? projectProjection = null) =>
        ApplyCore(scope, context, command, depthLimit, childLimit, accountContext, null, expiryPolicy, null, projectProjection);

    internal static CommandResult ApplyPreparedTitle(IWriteScope scope, CommandContext context,
        SessionCommand command, int depthLimit, int childLimit, string accountContext, byte[]? titleUtf8, MessageExpiryPolicy? expiryPolicy = null, PreparedStartEffectPayload? startEffectPayload = null, ISessionProjectProjection? projectProjection = null) =>
        ApplyCore(scope, context, command, depthLimit, childLimit, accountContext, titleUtf8, expiryPolicy, startEffectPayload, projectProjection);

    private static CommandResult ApplyCore(IWriteScope scope, CommandContext context,
        SessionCommand command, int depthLimit, int childLimit, string accountContext, byte[]? titleUtf8, MessageExpiryPolicy? expiryPolicy, PreparedStartEffectPayload? startEffectPayload, ISessionProjectProjection? projectProjection)
    {
        if (string.IsNullOrWhiteSpace(context.EntityId))
            return Refused(context, "bad_args");
        if (command is EstablishForeignParent) return Refused(context, "forbidden");
        var current = Find(scope, context.EntityId);
        if (command is RegisterForeignSession foreign)
            return RegisterForeign(scope, context, foreign, current, titleUtf8);
        if (current?.State == "foreign")
            return Refused(context, "invalid_transition", current);
        if (current is not null && current.Machine != context.Machine)
            return Refused(context, "wrong_machine", current);
        if (current?.State == "handing_off")
            return Refused(context, "handing_off", current);
        if (current is not null && current.Epoch != context.Epoch)
            return Refused(context, "stale_epoch", current);
        if ((current?.Version ?? 0) != context.ExpectedVersion)
            return Refused(context, "version_conflict", current);
        if (command is StartSession start)
            return current is null ? Start(scope, context, start, depthLimit, childLimit, accountContext, titleUtf8, startEffectPayload) : Refused(context, "invalid_transition", current);
        if (current is null)
            return Refused(context, "bad_args");
        return command switch
        {
            AssignProject assignment => Assign(scope, context, current, assignment, projectProjection),
            SetSessionState fact => SetState(scope, context, current, fact, expiryPolicy),
            ReportCapabilities capabilities => Capabilities(scope, context, current, capabilities),
            StopSession => Stop(scope, context, current),
            LinkTask link => Link(scope, context, current, link),
            ReportSessionModel model => ReportModel(scope, context, current, model),
            ReportSessionTitle title => ReportTitle(scope, context, current, title, titleUtf8),
            _ => Refused(context, "unsupported_command", current)
        };
    }

    private static CommandResult Assign(IWriteScope scope, CommandContext context, Session current, AssignProject command, ISessionProjectProjection? projection)
    {
        if (context.Actor.Kind != ActorKind.System) return Refused(context, "forbidden", current);
        if (string.IsNullOrWhiteSpace(command.ProjectId)) return Refused(context, "bad_args", current);
        if (current.ProjectId is not null)
            return current.ProjectId == command.ProjectId ? new(true, false, null, current.Version, 0, current.Epoch)
                : Refused(context, "project_already_assigned", current);
        if (projection is null) throw new InvalidOperationException("The session project projection is required.");
        scope.Execute("UPDATE engine_session SET project_id=$project,version=version+1,updated_ms=$now WHERE id=$id",
            new("$project", command.ProjectId), new("$id", context.EntityId), new("$now", context.NowMs));
        projection.Assigned(scope, context.EntityId, command.ProjectId);
        return EngineJournal.Commit(scope, context, current.Version + 1, current.Epoch,
            [new("session.changed", "session", context.EntityId, current.Version + 1, false,
                new(context.EntityId, current.RunId, null, current.ParentId, current.State, current.Epoch))], []);
    }

    private static CommandResult RegisterForeign(IWriteScope scope, CommandContext context, RegisterForeignSession command, Session? current, byte[]? titleUtf8)
    {
        if (string.IsNullOrWhiteSpace(command.Machine) || command.Machine == context.Machine ||
            context.Actor != new EngineActor(ActorKind.System, "machine:" + command.Machine))
            return Refused(context, "forbidden", current);
        if (context.ExpectedVersion != 0 || context.Epoch != 0 || string.IsNullOrWhiteSpace(command.Provider) || command.Title is null)
            return Refused(context, "bad_args", current);
        if (current is not null)
            return current.State == "foreign" && current.Machine == command.Machine
                ? new(true, false, null, current.Version, 0, current.Epoch) : Refused(context, "forbidden", current);
        scope.Execute("""
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,provider,model,folder,title,state,
                activity_state,holder,epoch,cancel_requested,account_context,managed_checkout)
            VALUES($id,0,$machine,$now,$now,$provider,NULL,'',CAST($title AS TEXT),'foreign','idle',$machine,0,0,'',0)
            """, new("$id", context.EntityId), new("$machine", command.Machine), new("$now", context.NowMs),
            new("$provider", command.Provider), new("$title", titleUtf8 ?? new System.Text.UTF8Encoding(false, true).GetBytes(command.Title)));
        return new(true, false, null, 0, 0, 0);
    }

    private static CommandResult Start(IWriteScope scope, CommandContext context, StartSession command, int depthLimit, int childLimit, string accountContext, byte[]? titleUtf8, PreparedStartEffectPayload? startEffectPayload)
    {
        var untitledOwnerRoot = command.Title == "" && context.Actor == new EngineActor(ActorKind.Window, "owner") &&
            command.ParentId is null && command.RunId is null;
        if (!Assembly.Application.Common.Agents.AccountContext.IsValid(accountContext) || string.IsNullOrWhiteSpace(command.Provider) || command.Model is not null && string.IsNullOrWhiteSpace(command.Model) ||
            string.IsNullOrWhiteSpace(command.Folder) || string.IsNullOrWhiteSpace(command.Title) && !untitledOwnerRoot)
            return Refused(context, "bad_args");
        var peer = context.Actor.Kind == ActorKind.System && context.Actor.Id.StartsWith("machine:", StringComparison.Ordinal);
        if (!peer && context.Actor.Kind != ActorKind.Window &&
            (context.Actor.Kind != ActorKind.Session || context.Actor.Id != command.ParentId))
            return Refused(context, "forbidden");
        Session? parent = null;
        if (command.ParentId is not null)
        {
            parent = Find(scope, command.ParentId);
            if (parent is null)
                return Refused(context, "bad_args");
            if (parent.State == "foreign" || parent.Machine != context.Machine)
            {
                if (!peer || parent.State != "foreign" || parent.Machine == context.Machine || context.Actor.Id != "machine:" + parent.Machine)
                    return Refused(context, "forbidden");
            }
            else if (peer) return Refused(context, "forbidden");
            if (!peer)
            {
                var ancestry = scope.QueryFirst("""
                    WITH RECURSIVE ancestors(id,parent_id) AS (
                        SELECT id,parent_id FROM engine_session WHERE id=$parent
                        UNION
                        SELECT p.id,p.parent_id
                        FROM engine_session p JOIN ancestors a ON p.id=a.parent_id
                        LIMIT $ancestorBudget
                    )
                    SELECT COUNT(*),COALESCE(MAX(CASE WHEN parent_id IS NULL THEN 1 ELSE 0 END),0)
                    FROM ancestors
                    """, r => (Count: r.Long(0), Root: r.Long(1)),
                    new("$parent", command.ParentId), new("$ancestorBudget", (long)depthLimit + 1L));
                if (ancestry.Count > depthLimit)
                    return Refused(context, "depth_limit");
                if (ancestry.Count == 0 || ancestry.Root == 0)
                    return Refused(context, "bad_args");
            }
            if (parent.State == "ended")
                return Refused(context, "invalid_transition");
        }
        if (peer && (parent is null || command.RunId is not null)) return Refused(context, "forbidden");
        if (command.ManagedCheckout == true && context.Actor.Kind == ActorKind.Session && scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_session WHERE id=$parent AND machine=$machine AND holder='local'
                  AND managed_checkout=1 AND folder=$folder
                """, r => r.Long(0), new("$parent", command.ParentId), new("$machine", context.Machine), new("$folder", command.Folder)) != 1)
            return Refused(context, "bad_args");
        if (command.RunId is not null)
        {
            var owner = scope.QueryFirst("SELECT owner_session_id FROM engine_run WHERE id=$id",
                r => r.Text(0), new SqlArg("$id", command.RunId));
            if (owner is null)
                return Refused(context, "bad_args");
            if (owner != command.ParentId)
                return Refused(context, "forbidden");
        }
        if (!peer && parent is not null && scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_session WHERE parent_id=$parent AND state != 'ended'
                """, r => r.Long(0), new SqlArg("$parent", command.ParentId)) >= childLimit)
            return Refused(context, "child_limit");
        if (context.Epoch != 0)
            return new(false, false, "stale_epoch", 0, 0, 0);
        var startPayload = startEffectPayload is null ? JsonSerializer.SerializeToElement(command, EngineJson.Default.StartSession)
            : startEffectPayload.ForExact(command);
        scope.Execute("""
            INSERT INTO engine_session(id,version,machine,created_ms,updated_ms,task_url,
                provider,model,folder,title,state,activity_state,parent_id,run_id,holder,epoch,cancel_requested,account_context,managed_checkout)
            VALUES($id,1,$machine,$now,$now,$task,$provider,$model,$folder,CAST($title AS TEXT),
                'starting','idle',$parent,$run,'local',0,0,$account,$managed)
            """, new("$id", context.EntityId), new("$machine", context.Machine), new("$now", context.NowMs),
            new("$task", command.TaskUrl), new("$provider", command.Provider), new("$model", command.Model),
            new("$folder", command.Folder), new("$title", titleUtf8 ?? new System.Text.UTF8Encoding(false, true).GetBytes(command.Title)), new("$parent", command.ParentId), new("$run", command.RunId), new("$account", accountContext), new("$managed", command.ManagedCheckout == true ? 1 : 0));
        var created = new Session(1, 0, "starting", "idle", command.ParentId, command.RunId, context.Machine);
        return Commit(scope, context, created, "session.started",
            [new("", "start", context.EntityId, command.RunId, startPayload)]);
    }

    private static CommandResult Capabilities(IWriteScope scope, CommandContext context, Session current, ReportCapabilities command)
    {
        if (context.Actor.Kind != ActorKind.System)
            return Refused(context, "forbidden", current);
        if (current.State is "ended" or "foreign")
            return Refused(context, "invalid_transition", current);
        if (scope.QueryFirst("SELECT machine FROM engine_session WHERE id=$id", r => r.Text(0),
                new SqlArg("$id", context.EntityId)) != context.Machine)
            return Refused(context, "wrong_machine", current);
        var changed = scope.Execute("""
            UPDATE engine_session SET can_steer=$steer,version=version+1,updated_ms=$now
            WHERE id=$id AND machine=$machine AND can_steer<>$steer
            """, new("$steer", command.CanSteer ? 1 : 0), new("$now", context.NowMs),
            new("$id", context.EntityId), new("$machine", context.Machine));
        return changed == 0 ? new(true, false, null, current.Version, 0, current.Epoch)
            : Commit(scope, context, current with { Version = current.Version + 1 }, "session.changed", []);
    }

    private static CommandResult SetState(IWriteScope scope, CommandContext context, Session current, SetSessionState command, MessageExpiryPolicy? expiryPolicy, bool checkedWorkflowRelease = false)
    {
        if (context.Actor.Kind != ActorKind.System)
            return Refused(context, "forbidden", current);
        if (string.IsNullOrWhiteSpace(command.State))
            return Refused(context, "bad_args", current);
        var from = current.State == "needs_me" ? current.Activity : current.State;
        var stopAcknowledged = current.CancelRequested && command.State is "idle" or "ended";
        var allowed = (from, command.State) is ("starting", "idle") or ("idle", "running") or ("running", "idle")
            or ("starting" or "idle" or "running", "lost" or "ended") or ("lost", "idle")
            || from == "idle" && command.State == "idle" && stopAcknowledged
            || checkedWorkflowRelease && from == "lost" && command.State == "ended";
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
                provider_session_id=COALESCE($provider,provider_session_id),lost_ms=$lost,
                cancel_requested=CASE WHEN $ack=1 THEN 0 ELSE cancel_requested END WHERE id=$id
            """, new("$version", updated.Version), new("$now", context.NowMs), new("$state", state),
            new("$activity", activity), new("$provider", command.ProviderSessionId),
            new("$lost", command.State == "lost" ? context.NowMs : null), new("$id", context.EntityId),
            new("$ack", stopAcknowledged ? 1 : 0));
        List<EffectDraft> effects = [];
        List<EventDraft> events = [];
        if (stopAcknowledged)
            events.AddRange(MessageTransitions.ReleaseHeld(scope, context, context.EntityId, command.State == "ended", effects));
        if (command.State == "ended")
        {
            events.AddRange(MessageTransitions.ChildEnded(scope, context, context.EntityId, effects, expiryPolicy));
        }
        if (command.State is "idle" or "running" or "ended")
            events.AddRange(MessageRelease.Next(scope, context, context.EntityId, effects));
        return Commit(scope, context, updated, "session.changed", effects, events, command.Reason);
    }

    private static CommandResult ReportTitle(IWriteScope scope, CommandContext context, Session current, ReportSessionTitle command, byte[]? titleUtf8)
    {
        if (context.Actor.Kind != ActorKind.System) return Refused(context, "forbidden", current);
        if (command.Title is null) return Refused(context, "bad_args", current);
        var title = scope.QueryFirst("SELECT CAST(title AS BLOB) FROM engine_session WHERE id=$id", r => new System.Text.UTF8Encoding(false, true).GetString(r.Blob(0)), new SqlArg("$id", context.EntityId));
        if (title == command.Title) return new(true, false, null, current.Version, 0, current.Epoch);
        scope.Execute("UPDATE engine_session SET title=CAST($title AS TEXT),version=version+1,updated_ms=$now WHERE id=$id",
            new("$title", titleUtf8 ?? new System.Text.UTF8Encoding(false, true).GetBytes(command.Title)), new("$now", context.NowMs), new("$id", context.EntityId));
        return EngineJournal.Commit(scope, context, current.Version + 1, current.Epoch,
            [new("session.changed", "session", context.EntityId, current.Version + 1, false,
                new(context.EntityId, current.RunId, null, current.ParentId, current.State, current.Epoch))], []);
    }

    private static CommandResult ReportModel(IWriteScope scope, CommandContext context, Session current, ReportSessionModel command)
    {
        if (context.Actor.Kind != ActorKind.System)
            return Refused(context, "forbidden", current);
        if (command.Model is not null && string.IsNullOrWhiteSpace(command.Model))
            return Refused(context, "bad_args", current);
        var model = scope.QueryFirst("SELECT model FROM engine_session WHERE id=$id", r => r.IsNull(0) ? null : r.Text(0), new SqlArg("$id", context.EntityId));
        if (model == command.Model) return new(true, false, null, current.Version, 0, current.Epoch);
        scope.Execute("UPDATE engine_session SET model=$model,version=version+1,updated_ms=$now WHERE id=$id",
            new("$model", command.Model), new("$now", context.NowMs), new("$id", context.EntityId));
        return EngineJournal.Commit(scope, context, current.Version + 1, current.Epoch,
            [new("session.changed", "session", context.EntityId, current.Version + 1, false,
                new(context.EntityId, current.RunId, null, current.ParentId, current.State, current.Epoch))], []);
    }

    private static CommandResult Link(IWriteScope scope, CommandContext context, Session current, LinkTask command)
    {
        if (context.Actor.Kind != ActorKind.Session || context.Actor.Id != context.EntityId)
            return Refused(context, "forbidden", current);
        if (current.State is "ended" or "lost") return Refused(context, "invalid_transition", current);
        if (!Uri.TryCreate(command.TaskUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || string.IsNullOrEmpty(url.Host))
            return Refused(context, "bad_args", current);
        scope.Execute("UPDATE engine_session SET task_url=$url,version=version+1,updated_ms=$now WHERE id=$id",
            new("$url", command.TaskUrl), new("$now", context.NowMs), new("$id", context.EntityId));
        return EngineJournal.Commit(scope, context, current.Version + 1, current.Epoch,
            [new("session.changed", "session", context.EntityId, current.Version + 1, false,
                new(context.EntityId, current.RunId, null, current.ParentId, current.State, current.Epoch))], []);
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
        string kind, IReadOnlyList<EffectDraft> effects, IReadOnlyList<EventDraft>? additionalEvents = null, string? reason = null)
    {
        List<EventDraft> events = [new(kind, "session", context.EntityId, session.Version, false,
            new(context.EntityId, session.RunId, null, session.ParentId, session.State, session.Epoch, reason))];
        if (additionalEvents is not null) events.AddRange(additionalEvents);
        var runs = scope.Query("""
            SELECT id FROM engine_run WHERE owner_session_id=$session
            UNION SELECT run_id FROM engine_step WHERE session_id=$session
            """, r => r.Text(0), new SqlArg("$session", context.EntityId));
        foreach (var run in runs) events.AddRange(RunTransitions.Recompute(scope, context, run));
        return EngineJournal.Commit(scope, context, session.Version, session.Epoch, events, effects);
    }

    public static IReadOnlyList<EventDraft> RecomputeNeeds(IWriteScope s, CommandContext c, string sessionId)
    {
        var session = Find(s, sessionId);
        if (session is null || session.State is not ("idle" or "running" or "needs_me")) return [];
        var open = s.QueryFirst("SELECT COUNT(*) FROM engine_needs_me WHERE session_id=$id AND state='open'",
            r => r.Long(0), new SqlArg("$id", sessionId)) > 0;
        var activity = session.State == "needs_me" ? session.Activity : session.State;
        var state = open ? "needs_me" : activity;
        if (state == session.State) return [];
        s.Execute("UPDATE engine_session SET state=$state,activity_state=$activity,version=version+1,updated_ms=$now WHERE id=$id",
            new("$state", state), new("$activity", activity), new("$now", c.NowMs), new("$id", sessionId));
        return [new("session.changed", "session", sessionId, session.Version + 1, false,
            new(sessionId, session.RunId, null, session.ParentId, state, session.Epoch))];
    }

    private static Session? Find(IWriteScope scope, string id) => scope.QueryFirst("""
        SELECT version,epoch,state,activity_state,parent_id,run_id,machine,cancel_requested,reserved_epoch,project_id FROM engine_session WHERE id=$id
        """, r => new Session(r.Long(0), r.Long(1), r.Text(2), r.Text(3),
            r.IsNull(4) ? null : r.Text(4), r.IsNull(5) ? null : r.Text(5), r.Text(6), r.Long(7) != 0, r.IsNull(8) ? null : r.Long(8), r.IsNull(9) ? null : r.Text(9)), new SqlArg("$id", id));

    private static CommandResult Refused(CommandContext context, string error, Session? current = null) =>
        new(false, false, error, current?.Version ?? 0, 0, current?.ReservedEpoch ?? current?.Epoch ?? context.Epoch);
}
