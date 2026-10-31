using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Sessions;

namespace Assembly.Application.Features.Engine.Runs;

public static class RunTransitions
{
    private sealed record Run(long Version, string Owner, string State, long Epoch);

    public static CommandResult Apply(IWriteScope s, CommandContext c, RunCommand command)
    {
        if (string.IsNullOrWhiteSpace(c.EntityId)) return Refused(c, "bad_args");
        var run = Find(s, c.EntityId);
        if (command is StartRun start && run is null) return Start(s, c, start);
        if (run is null) return Refused(c, "bad_args");
        if (c.Epoch != run.Epoch) return Refused(c, "stale_epoch", run);
        if (c.ExpectedVersion != run.Version) return Refused(c, "version_conflict", run);
        if (!CanControl(c, run.Owner)) return Refused(c, "forbidden", run);
        if (command is not CancelRun || IsTerminal(run.State) || run.State == "cancelling")
            return Refused(c, "invalid_transition", run);
        s.Execute("UPDATE engine_run SET state='cancelling',version=version+1,updated_ms=$now WHERE id=$id",
            new("$now", c.NowMs), new("$id", c.EntityId));
        List<EventDraft> events = [Changed(c.EntityId, run.Version + 1, run.Owner, "cancelling")];
        events.AddRange(s.Query("""
            UPDATE engine_step SET state='cancelled',version=version+1,updated_ms=$now,ended_ms=$now
            WHERE run_id=$id AND attempt=0 AND state='pending'
            RETURNING id,version,session_id
            """, r => StepCancelled(r.Text(0), r.Long(1), r.IsNull(2) ? null : r.Text(2), c.EntityId, run.Owner),
            new("$now", c.NowMs), new("$id", c.EntityId)));
        var stoppedSessions = s.Query("""
            UPDATE engine_session SET cancel_requested=1,version=version+1,updated_ms=$now
            WHERE state!='ended' AND id IN (SELECT session_id FROM engine_step WHERE run_id=$id AND attempt>0)
            RETURNING id,version,state,epoch,parent_id
            """, r => new EventDraft("session.cancel_requested", "session", r.Text(0), r.Long(1), false,
                new(r.Text(0), c.EntityId, null, r.IsNull(4) ? null : r.Text(4), r.Text(2), r.Long(3))),
            new("$now", c.NowMs), new("$id", c.EntityId));
        events.AddRange(stoppedSessions);
        var effects = stoppedSessions.Select(session => new EffectDraft("", "stop", session.EntityId, c.EntityId,
            JsonSerializer.SerializeToElement(new StopSession(), EngineJson.Default.StopSession))).ToArray();
        // Effects queued by this command must drain before cancellation can complete.
        if (effects.Length == 0) events.AddRange(Recompute(s, c, c.EntityId));
        var updated = Find(s, c.EntityId)!;
        return EngineJournal.Commit(s, c, updated.Version, updated.Epoch, events, effects);
    }

    private static CommandResult Start(IWriteScope s, CommandContext c, StartRun command)
    {
        var owner = s.QueryFirst("SELECT parent_id,state,epoch,lost_ms FROM engine_session WHERE id=$id",
            r => new { Parent = r.IsNull(0) ? null : r.Text(0), State = r.Text(1), Epoch = r.Long(2), Lost = r.IsNull(3) ? (long?)null : r.Long(3) },
            new SqlArg("$id", command.OwnerSessionId));
        if (owner is null || owner.Parent is not null || owner.State == "ended" || command.OwnerLossLimitMs < 1 ||
            !Uri.TryCreate(command.TaskUrl, UriKind.Absolute, out var task) || task.Scheme != "https" ||
            !ValidDefinitions(s, command.Steps)) return Refused(c, "bad_args");
        if (!CanControl(c, command.OwnerSessionId)) return Refused(c, "forbidden");
        if (c.Epoch != owner.Epoch) return new(false, false, "stale_epoch", 0, 0, owner.Epoch);
        if (c.ExpectedVersion != 0) return Refused(c, "version_conflict");
        s.Execute("""
            INSERT INTO engine_run(id,version,machine,created_ms,updated_ms,task_url,owner_session_id,worktree,branch,state,owner_loss_limit_ms,owner_lost_ms)
            VALUES($id,1,$machine,$now,$now,$task,$owner,$worktree,$branch,'queued',$limit,$lost)
            """, new("$id", c.EntityId), new("$machine", c.Machine), new("$now", c.NowMs), new("$task", command.TaskUrl),
            new("$owner", command.OwnerSessionId), new("$worktree", command.Worktree), new("$branch", command.Branch),
            new("$limit", command.OwnerLossLimitMs), new("$lost", owner.Lost));
        foreach (var step in command.Steps)
            s.Execute("""
                INSERT INTO engine_step(id,version,machine,created_ms,updated_ms,task_url,run_id,name,state,attempt,max_attempts,required,rule_skippable)
                VALUES($id,1,$machine,$now,$now,$task,$run,$name,'pending',0,$max,$required,$skip)
                """, new("$id", step.Id), new("$machine", c.Machine), new("$now", c.NowMs), new("$task", command.TaskUrl),
                new("$run", c.EntityId), new("$name", step.Name), new("$max", step.MaxAttempts), new("$required", step.Required ? 1 : 0), new("$skip", step.RuleSkippable ? 1 : 0));
        foreach (var step in command.Steps)
            foreach (var dependency in step.Dependencies)
                s.Execute("INSERT INTO engine_step_dependency(step_id,dependency_id,optional) VALUES($step,$dep,$optional)",
                    new("$step", step.Id), new("$dep", dependency.StepId), new("$optional", dependency.Optional ? 1 : 0));
        return EngineJournal.Commit(s, c, 1, owner.Epoch,
            [Changed(c.EntityId, 1, command.OwnerSessionId, "queued")], []);
    }

    private static bool ValidDefinitions(IWriteScope s, IReadOnlyList<StepDefinition>? steps)
    {
        if (steps is null || steps.Count == 0 || steps.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) ||
            string.IsNullOrWhiteSpace(x.Name) || x.MaxAttempts < 1 || x.Dependencies is null)) return false;
        var definitions = new Dictionary<string, StepDefinition>();
        foreach (var step in steps)
            if (!definitions.TryAdd(step.Id, step)) return false;
        var ids = JsonSerializer.Serialize((IReadOnlyList<string>)definitions.Keys.ToArray(), EngineJson.Default.IReadOnlyListString);
        if (s.QueryFirst("SELECT COUNT(*) FROM engine_step WHERE id IN (SELECT value FROM json_each($ids))",
            r => r.Long(0), new SqlArg("$ids", ids)) > 0) return false;
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        bool Visit(string id)
        {
            if (visited.Contains(id)) return true;
            if (!visiting.Add(id)) return false;
            var deps = definitions[id].Dependencies;
            if (deps.Any(d => d is null || !definitions.ContainsKey(d.StepId)) || deps.Select(d => d.StepId).Distinct().Count() != deps.Count)
                return false;
            foreach (var dep in deps) if (!Visit(dep.StepId)) return false;
            visiting.Remove(id); visited.Add(id); return true;
        }
        return definitions.Keys.All(Visit);
    }

    public static IReadOnlyList<EventDraft> Recompute(IWriteScope s, CommandContext c, string runId)
    {
        var run = Find(s, runId);
        if (run is null || IsTerminal(run.State)) return [];
        List<EventDraft> events = [];
        var ownerChanged = s.QueryFirst("""
            SELECT r.owner_lost_ms IS NOT CASE WHEN se.state='lost' THEN se.lost_ms ELSE NULL END
            FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id WHERE r.id=$id
            """, r => r.Long(0) != 0, new SqlArg("$id", runId));
        s.Execute("""
            UPDATE engine_run SET owner_lost_ms=(SELECT CASE WHEN state='lost' THEN lost_ms ELSE NULL END FROM engine_session WHERE id=owner_session_id)
            WHERE id=$id
            """, new SqlArg("$id", runId));
        if (run.State == "cancelling")
        {
            events.AddRange(s.Query("""
                UPDATE engine_step SET state='cancelled',version=version+1,updated_ms=$now,ended_ms=$now
                WHERE run_id=$id AND attempt>0 AND state IN ('running','waiting')
                AND EXISTS(SELECT 1 FROM engine_session WHERE id=engine_step.session_id AND state='ended')
                RETURNING id,version,session_id
                """, r => StepCancelled(r.Text(0), r.Long(1), r.Text(2), runId, run.Owner),
                new("$now", c.NowMs), new("$id", runId)));
            s.Execute("""
                UPDATE engine_step_attempt SET state='cancelled',ended_ms=$now
                WHERE state IN ('running','waiting') AND EXISTS(SELECT 1 FROM engine_step st
                    WHERE st.id=step_id AND st.attempt=engine_step_attempt.attempt AND st.run_id=$id AND st.state='cancelled')
                """, new("$now", c.NowMs), new("$id", runId));
        }
        var next = s.QueryFirst("""
            SELECT CASE
              WHEN state='cancelling' THEN CASE WHEN
                NOT EXISTS(SELECT 1 FROM engine_step st JOIN engine_session se ON se.id=st.session_id
                    WHERE st.run_id=$id AND st.attempt>0 AND se.state!='ended')
                AND NOT EXISTS(SELECT 1 FROM engine_outbox WHERE run_id=$id AND state!='done')
                THEN 'cancelled' ELSE 'cancelling' END
              WHEN EXISTS(SELECT 1 FROM engine_step WHERE run_id=$id AND required=1 AND state='failed' AND attempt>=max_attempts) THEN 'failed'
              WHEN NOT EXISTS(SELECT 1 FROM engine_step WHERE run_id=$id AND required=1 AND state NOT IN ('succeeded','skipped')) THEN 'succeeded'
              WHEN EXISTS(SELECT 1 FROM engine_needs_me WHERE run_id=$id AND state='open') THEN 'needs_me'
              WHEN EXISTS(SELECT 1 FROM engine_step WHERE run_id=$id AND attempt>0) THEN 'running'
              ELSE 'queued' END FROM engine_run WHERE id=$id
            """, r => r.Text(0), new SqlArg("$id", runId))!;
        if (next == run.State && !ownerChanged) return events;
        s.Execute("UPDATE engine_run SET state=$state,version=version+1,updated_ms=$now WHERE id=$id",
            new("$state", next), new("$now", c.NowMs), new("$id", runId));
        events.Add(Changed(runId, run.Version + 1, run.Owner, next));
        return events;
    }

    public static CommandResult ExpireLostOwner(IWriteScope s, CommandContext c, string runId)
    {
        var run = Find(s, runId);
        if (run is null || c.EntityId != runId) return Refused(c, "bad_args");
        if (c.Actor.Kind != ActorKind.System) return Refused(c, "forbidden", run);
        if (c.Epoch != run.Epoch) return Refused(c, "stale_epoch", run);
        if (c.ExpectedVersion != run.Version) return Refused(c, "version_conflict", run);
        if (IsTerminal(run.State) || run.State == "cancelling") return Refused(c, "invalid_transition", run);
        var expired = s.QueryFirst("""
            SELECT COUNT(*) FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id
            WHERE r.id=$id AND se.state='lost' AND r.owner_lost_ms IS NOT NULL
              AND $now>=r.owner_lost_ms AND $now-r.owner_lost_ms>=r.owner_loss_limit_ms
            """, r => r.Long(0), new("$id", runId), new("$now", c.NowMs));
        if (expired == 0) return Refused(c, "owner_not_expired", run);
        s.Execute("UPDATE engine_run SET state='failed',version=version+1,updated_ms=$now WHERE id=$id", new("$now", c.NowMs), new("$id", runId));
        return EngineJournal.Commit(s, c, run.Version + 1, run.Epoch, [Changed(runId, run.Version + 1, run.Owner, "failed")], []);
    }

    internal static bool IsTerminal(string state) => state is "succeeded" or "failed" or "cancelled";
    internal static bool CanControl(CommandContext c, string owner) => c.Actor.Kind == ActorKind.Window ||
        c.Actor.Kind == ActorKind.Session && c.Actor.Id == owner;
    private static Run? Find(IWriteScope s, string id) => s.QueryFirst("""
        SELECT r.version,r.owner_session_id,r.state,se.epoch FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id WHERE r.id=$id
        """, r => new Run(r.Long(0), r.Text(1), r.Text(2), r.Long(3)), new SqlArg("$id", id));
    private static EventDraft StepCancelled(string id, long version, string? session, string run, string owner) =>
        new("step.changed", "step", id, version, false, new(session, run, id, owner, "cancelled", null));
    private static EventDraft Changed(string id, long version, string owner, string state) =>
        new("run.changed", "run", id, version, false, new(owner, id, null, null, state, null));
    private static CommandResult Refused(CommandContext c, string error, Run? run = null) =>
        new(false, false, error, run?.Version ?? 0, 0, run?.Epoch ?? c.Epoch);
}
