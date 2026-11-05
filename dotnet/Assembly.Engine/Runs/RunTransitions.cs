using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;
using Assembly.Engine.Workflows.Runs;
using Assembly.Engine.Workflows.Execution;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Engine.Runs;

public static class RunTransitions
{
    // Actual checked release of the current immutable attempt; ended alone is not proof.
    private const string WorkflowUnreleasedWorker = $"""
        SELECT 1 FROM engine_step st LEFT JOIN engine_session worker ON worker.id=st.session_id
                    WHERE st.run_id=$id AND st.session_id IS NOT NULL AND (
                      worker.state IS NULL OR worker.state<>'ended' OR (NOT EXISTS(
                        SELECT 1 FROM engine_step_attempt a JOIN engine_workflow_receipt receipt ON receipt.id=a.receipt_id
                        JOIN engine_workflow_history h ON h.run_id=st.run_id AND h.step_id=st.id
                          AND h.attempt=st.attempt AND h.generation=st.generation AND h.kind='workflow_worker_released'
                        WHERE a.step_id=st.id AND a.attempt=st.attempt AND a.generation=st.generation
                          AND receipt.state='accepted' AND receipt.run_id=st.run_id AND receipt.step_id=st.id
                          AND receipt.attempt=st.attempt AND receipt.generation=st.generation
                          AND receipt.session_id=st.session_id AND receipt.epoch=a.execution_epoch
                          AND a.session_id=st.session_id AND a.execution_epoch=worker.epoch
                          AND st.authority_session_id=worker.id AND st.authority_epoch=a.execution_epoch
                          AND st.authority_machine=worker.machine AND a.authority_machine=worker.machine
                          AND receipt.machine=worker.machine AND h.machine=worker.machine AND st.workspace_id=a.workspace_id
                          AND json_extract(h.safe_body_json,'$.receiptId')=receipt.id
                          AND json_extract(h.safe_body_json,'$.sessionId')=st.session_id
                          AND json_extract(h.safe_body_json,'$.epoch')=a.execution_epoch
                          AND json_extract(h.safe_body_json,'$.workspaceId')=a.workspace_id)
                        AND NOT ((st.state IN ('failed','cancelled') OR $runCancelling=1) AND EXISTS(
                          SELECT 1 FROM engine_step_attempt attempt
                          WHERE attempt.step_id=st.id AND attempt.attempt=st.attempt AND attempt.generation=st.generation
                            AND attempt.session_id=worker.id AND attempt.execution_epoch=worker.epoch
                            AND attempt.authority_machine=worker.machine AND st.authority_machine=worker.machine
                            AND st.authority_session_id=worker.id AND st.authority_epoch=attempt.execution_epoch
                            AND st.workspace_id=attempt.workspace_id AND {WorkflowExecutionReleases.ExactAttemptPredicate}))))
        """;

    private sealed record Run(long Version, string Owner, string State, long Epoch, bool Workflow, string Machine);

    public static CommandResult Apply(IWriteScope s, CommandContext c, RunCommand command)
    {
        if (string.IsNullOrWhiteSpace(c.EntityId)) return Refused(c, "bad_args");
        var run = Find(s, c.EntityId);
        if (command is StartRun start && run is null) return Start(s, c, start);
        if (run is null) return Refused(c, "bad_args");
        if (run.Machine != c.Machine) return Refused(c, "wrong_machine", run);
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
            WHERE run_id=$id AND (state='pending' OR kind='gate' AND state='waiting')
            RETURNING id,version,session_id
            """, r => StepCancelled(r.Text(0), r.Long(1), r.IsNull(2) ? null : r.Text(2), c.EntityId, run.Owner),
            new("$now", c.NowMs), new("$id", c.EntityId)));
        events.AddRange(s.Query("""
            UPDATE engine_needs_me SET state='cancelled',version=version+1,updated_ms=$now
            WHERE run_id=$id AND source_kind='workflow_gate' AND state='open' RETURNING id,version,step_id
            """, r => new EventDraft("needs_me.cancelled", "needs_me", r.Text(0), r.Long(1), false,
                new(run.Owner, c.EntityId, r.IsNull(2) ? null : r.Text(2), run.Owner, "cancelled", run.Epoch)), new SqlArg("$id", c.EntityId), new("$now", c.NowMs)));
        s.Execute("""
            UPDATE engine_step_attempt SET state='cancelled',ended_ms=$now,gate_state='settled'
            WHERE state='waiting' AND EXISTS(SELECT 1 FROM engine_step st WHERE st.id=step_id AND st.run_id=$run
              AND st.attempt=engine_step_attempt.attempt AND st.kind='gate' AND st.state='cancelled')
            """, new("$now", c.NowMs), new("$run", c.EntityId));
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

    internal static CommandResult StartWorkflow(IWriteScope s, CommandContext c, StartRun command, WorkflowSetupIntent setup)
    {
        if (Find(s, c.EntityId) is not null) return Refused(c, "version_conflict");
        var payload = JsonSerializer.SerializeToElement(setup, EngineJson.Default.WorkflowSetupIntent);
        return Start(s, c, command, [new("", "workflow_setup", null, c.EntityId, payload)]);
    }

    private static CommandResult Start(IWriteScope s, CommandContext c, StartRun command, IReadOnlyList<EffectDraft>? effects = null)
    {
        var owner = s.QueryFirst("SELECT parent_id,state,epoch,lost_ms FROM engine_session WHERE id=$id",
            r => new { Parent = r.IsNull(0) ? null : r.Text(0), State = r.Text(1), Epoch = r.Long(2), Lost = r.IsNull(3) ? (long?)null : r.Long(3) },
            new SqlArg("$id", command.OwnerSessionId));
        if (owner is null || owner.Parent is not null || owner.State == "ended" || command.OwnerLossLimitMs < 1 ||
            command.TaskUrl is not null && (!Uri.TryCreate(command.TaskUrl, UriKind.Absolute, out var task) || task.Scheme != "https") ||
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
            [Changed(c.EntityId, 1, command.OwnerSessionId, "queued"),
             .. command.Steps.Select(step => StepPending(step.Id, c.EntityId, command.OwnerSessionId))], effects ?? []);
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
        if (run is null || run.Machine != c.Machine || IsTerminal(run.State)) return [];
        var commandsDrained = !run.Workflow || CommandStepTransitions.ExecutionsDrained(s, runId);
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
        var next = s.QueryFirst($"""
            SELECT CASE
              WHEN state='cancelling' THEN CASE WHEN
                NOT EXISTS(SELECT 1 FROM engine_step st JOIN engine_session se ON se.id=st.session_id
                    WHERE st.run_id=$id AND st.attempt>0 AND st.state IN ('running','waiting') AND se.state!='ended')
                AND NOT EXISTS(SELECT 1 FROM engine_outbox WHERE run_id=$id AND state NOT IN ('done','failed','dead'))
                AND (definition_hash IS NULL OR $commandsDrained=1 AND NOT EXISTS({WorkflowUnreleasedWorker}))
                THEN 'cancelled' ELSE 'cancelling' END
              WHEN EXISTS(SELECT 1 FROM engine_step WHERE run_id=$id AND required=1 AND state='failed' AND CASE WHEN definition_hash IS NULL THEN attempt ELSE attempt_in_generation END>=max_attempts) THEN 'failed'
              WHEN NOT EXISTS(SELECT 1 FROM engine_step WHERE run_id=$id AND required=1 AND state NOT IN ('succeeded','skipped'))
                AND (definition_hash IS NULL OR (
                  NOT EXISTS(SELECT 1 FROM engine_step st WHERE st.run_id=$id AND (
                    st.state NOT IN ('succeeded','failed','skipped','cancelled')
                    OR st.required=1 AND st.state='skipped' AND (st.rule_skippable=0 OR st.outcome_reason IS NULL)
                    OR COALESCE(st.dispatch_state,'')='redo_pending'))
                  AND NOT EXISTS(SELECT 1 FROM engine_outbox WHERE run_id=$id AND state NOT IN ('done','failed','dead'))
                  AND $commandsDrained=1
                  AND NOT EXISTS({WorkflowUnreleasedWorker})
                )) THEN 'succeeded'
              WHEN EXISTS(SELECT 1 FROM engine_needs_me WHERE run_id=$id AND state='open') THEN 'needs_me'
              WHEN EXISTS(SELECT 1 FROM engine_step WHERE run_id=$id AND attempt>0) THEN 'running'
              ELSE 'queued' END FROM engine_run WHERE id=$id
            """, r => r.Text(0), new SqlArg("$id", runId), new("$commandsDrained", commandsDrained ? 1 : 0),
                new("$runCancelling", run.State == "cancelling" ? 1 : 0))!;
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
        if (run.Machine != c.Machine) return Refused(c, "wrong_machine", run);
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
        List<EventDraft> events = [Changed(runId, run.Version + 1, run.Owner, "failed")];
        List<EffectDraft> effects = [];
        if (run.Workflow)
        {
            events[0] = events[0] with { Data = events[0].Data with { Reason = "owner_lost" } };
            s.Execute("""
                INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,generation,kind,at_ms,safe_body_json)
                SELECT $id,1,$machine,$now,$now,$run,
                  (SELECT COALESCE(MAX(history_seq),0)+1 FROM engine_workflow_history WHERE run_id=$run),0,'owner_lost',$now,
                  json_object('reason','owner_lost','ownerSessionId',owner_session_id,'ownerEpoch',$epoch,
                    'lostMs',owner_lost_ms,'deadlineMs',owner_lost_ms+owner_loss_limit_ms)
                FROM engine_run WHERE id=$run
                """, new("$id", c.CommandId.ToString("N") + "/owner-lost"), new("$machine", c.Machine),
                new("$now", c.NowMs), new("$run", runId), new("$epoch", run.Epoch));
            // Failure is final immediately; stopping the original executors remains owned, durable cleanup.
            var stopped = s.Query("""
                UPDATE engine_session SET cancel_requested=1,version=version+1,updated_ms=$now
                WHERE machine=$machine AND holder='local' AND reserved_epoch IS NULL
                  AND parent_id=$owner AND run_id=$run AND state!='ended' AND cancel_requested=0
                  AND EXISTS(SELECT 1 FROM engine_step st JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
                    WHERE st.run_id=$run AND st.kind IN ('agent','review') AND st.session_id=engine_session.id
                      AND st.authority_session_id=engine_session.id AND st.authority_machine=$machine AND st.authority_epoch=engine_session.epoch
                      AND a.session_id=engine_session.id AND a.authority_machine=$machine AND a.execution_epoch=engine_session.epoch
                      AND a.generation=st.generation)
                RETURNING id,version,state,epoch,parent_id
                """, r => new EventDraft("session.cancel_requested", "session", r.Text(0), r.Long(1), false,
                    new(r.Text(0), runId, null, r.Text(4), r.Text(2), r.Long(3))),
                new("$machine", c.Machine), new("$owner", run.Owner), new("$run", runId), new("$now", c.NowMs));
            events.AddRange(stopped);
            effects.AddRange(stopped.Select(session => new EffectDraft("", "stop", session.EntityId, runId,
                JsonSerializer.SerializeToElement(new StopSession(), EngineJson.Default.StopSession))));
            s.Execute("""
                UPDATE engine_step_attempt SET cancel_requested=1 WHERE cancel_requested=0 AND authority_machine=$machine
                  AND EXISTS(SELECT 1 FROM engine_step st WHERE st.id=step_id AND st.run_id=$run AND st.kind='command'
                    AND st.authority_machine=$machine AND st.attempt=engine_step_attempt.attempt
                    AND st.generation=engine_step_attempt.generation AND st.authority_epoch=engine_step_attempt.execution_epoch
                    AND st.state IN ('running','waiting'))
                """, new("$machine", c.Machine), new("$run", runId));
        }
        return EngineJournal.Commit(s, c, run.Version + 1, run.Epoch, events, effects);
    }

    internal static bool IsTerminal(string state) => state is "succeeded" or "failed" or "cancelled";
    internal static bool CanControl(CommandContext c, string owner) => c.Actor.Kind == ActorKind.Window ||
        c.Actor.Kind == ActorKind.Session && c.Actor.Id == owner;
    private static Run? Find(IWriteScope s, string id) => s.QueryFirst("""
        SELECT r.version,r.owner_session_id,r.state,se.epoch,r.definition_hash IS NOT NULL,r.machine FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id WHERE r.id=$id
        """, r => new Run(r.Long(0), r.Text(1), r.Text(2), r.Long(3), r.Long(4) != 0, r.Text(5)), new SqlArg("$id", id));
    private static EventDraft StepPending(string id, string run, string owner) =>
        new("step.changed", "step", id, 1, false, new(null, run, id, owner, "pending", null));
    private static EventDraft StepCancelled(string id, long version, string? session, string run, string owner) =>
        new("step.changed", "step", id, version, false, new(session, run, id, owner, "cancelled", null));
    private static EventDraft Changed(string id, long version, string owner, string state) =>
        new("run.changed", "run", id, version, false, new(owner, id, null, null, state, null));
    private static CommandResult Refused(CommandContext c, string error, Run? run = null) =>
        new(false, false, error, run?.Version ?? 0, 0, run?.Epoch ?? c.Epoch);
}
