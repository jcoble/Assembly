using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Workflows.Execution;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Reviews;

namespace Assembly.Engine.Workflows.Runs;

internal static partial class WorkflowControlTransitions
{
    private static CommandResult StepControl(IWriteScope s, CommandContext c, Run run, WorkflowCommand command, PreparedWorkflowCommandRetry? preparedRetry)
    {
        CommandResult Refuse(string error) => new(false, false, error, run.Version, 0, run.Epoch);
        var id = command switch { RetryWorkflowStep retry => retry.StepId, DecideWorkflowStep decide => decide.StepId, GrantWorkflowScope grant => grant.StepId, _ => null };
        var step = s.QueryFirst("""
            SELECT id,version,state,kind,outcome_reason,attempt,generation,attempt_in_generation,max_attempts,
              session_id,COALESCE(authority_epoch,0),required,rule_skippable,required_scope,scope_granted
            FROM engine_step WHERE id=$step AND run_id=$run
            """, r => new
        {
            Id = r.Text(0),
            Version = r.Long(1),
            State = r.Text(2),
            Kind = r.IsNull(3) ? null : r.Text(3),
            Reason = r.IsNull(4) ? null : r.Text(4),
            Attempt = (int)r.Long(5),
            Generation = (int)r.Long(6),
            Used = r.Long(7),
            Max = r.Long(8),
            Session = r.IsNull(9) ? null : r.Text(9),
            Epoch = r.Long(10),
            Required = r.Long(11) != 0,
            Skippable = r.Long(12) != 0,
            Scope = r.IsNull(13) ? null : r.Text(13),
            Granted = r.Long(14) != 0
        },
            new SqlArg("$step", id), new("$run", c.EntityId));
        if (step is null) return Refuse("bad_args");
        var need = s.QueryFirst("SELECT id,version,source_kind FROM engine_needs_me WHERE run_id=$run AND step_id=$step AND state='open' ORDER BY created_ms,id LIMIT 1",
            r => new { Id = r.Text(0), Version = r.Long(1), Source = r.Text(2) }, new SqlArg("$run", c.EntityId), new("$step", id));
        if (command is GrantWorkflowScope scope)
        {
            if (c.Actor.Kind != ActorKind.Window || c.Origin?.Channel == "phone") return Refuse("forbidden");
            if (step.State != "waiting" || step.Scope is null || scope.Scope != step.Scope || step.Granted) return Refuse("invalid_transition");
            // Confirmation is durable presentation state only. It grants no external capability or permission.
            s.Execute("""
                INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
                SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'scope_confirmed',$now,
                  json_object('scope',$scope,'confirmedBy',$actor) FROM engine_workflow_history WHERE run_id=$run
                """, new("$id", c.CommandId.ToString("N") + "/scope"), new("$machine", c.Machine), new("$now", c.NowMs), new("$run", c.EntityId),
                new("$step", id), new("$attempt", step.Attempt), new("$generation", step.Generation), new("$scope", scope.Scope), new("$actor", c.Actor.Id));
            s.Execute("UPDATE engine_step SET scope_granted=1,version=version+1,updated_ms=$now WHERE id=$step", new("$now", c.NowMs), new("$step", id));
            return Changed(s, c, run, [StepEvent("workflow.scope_confirmed", step.State)], []);
        }
        var decision = command is RetryWorkflowStep ? "retry" : ((DecideWorkflowStep)command).Decision;
        if (decision is not ("approve" or "revise" or "deny" or "retry" or "skip" or "continue" or "cancel")) return Refuse("bad_args");
        if (step.Kind == "gate" && c.Actor.Kind != ActorKind.Window || step.Kind == "gate" && c.Origin?.Channel == "phone") return Refuse("forbidden");
        if (command is DecideWorkflowStep && step.Kind == "gate" && (need is null || step.State != "waiting")) return Refuse("already_resolved");
        if (need is not null && need.Source == "workflow" && step.Kind == "command" && decision is "approve" or "deny" or "cancel")
        {
            var choice = decision == "approve" ? "allow_once" : decision;
            var result = WorkflowExecutionNeeds.Resolve(s, c with { EntityId = need.Id, ExpectedVersion = need.Version },
                new ResolveNeed("answered", JsonSerializer.SerializeToElement(choice, EngineJson.Default.String)), c.Actor.Kind == ActorKind.Window, c.Actor.Kind == ActorKind.Session);
            if (!result.Applied) return Refuse(result.Error!);
            return CommitRun(s, c, run);
        }
        if (decision == "cancel") return RunTransitions.Apply(s, c, new CancelRun());
        if (decision == "retry")
        {
            if (step.State is not ("failed" or "waiting") || step.State == "waiting" && step.Reason is not ("session_lost" or "timed_out" or "interrupted" or "command_uncertain")) return Refuse("invalid_transition");
            if (step.State == "failed" && step.Used >= step.Max) return Refuse("attempts_exhausted");
            if (step.Kind == "command" && step.Reason == "command_uncertain")
            {
                if (preparedRetry is null) return Refuse("executor_quiescence_required");
                if (preparedRetry.Body.Intent.StepId != id || preparedRetry.Body.Intent.RunId != c.EntityId || preparedRetry.Context.Actor != c.Actor)
                    return Refuse("forbidden");
                var recovered = CommandRetryTransitions.Apply(s, preparedRetry.Context with { NowMs = c.NowMs }, preparedRetry);
                return recovered.Applied ? CommitRun(s, c, run) : Refuse(recovered.Error!);
            }
            if (step.Kind == "command" && step.Reason == "timed_out" && need is not null)
            {
                var result = CommandStepTransitions.AuthorizeTimeoutRetry(s, c with { EntityId = id!, ExpectedVersion = step.Version }, need.Id);
                return result.Applied ? CommitRun(s, c, run) : Refuse(result.Error!);
            }
            if (!CommandStepTransitions.ExecutionsDrained(s, c.EntityId, id, step.Attempt) || step.Session is not null &&
                (s.QueryFirst("SELECT state FROM engine_session WHERE id=$id", r => r.Text(0), new SqlArg("$id", step.Session)) != "ended" ||
                 !WorkflowExecutionReleases.IsReleased(s, new WorkflowExecutionFence(id!, step.Generation, step.Attempt, step.Epoch))))
                return Refuse("executor_quiescence_required");
        }
        else if (step.Kind == "gate" && decision is "approve" or "revise" or "deny")
        {
            if (step.State != "waiting" || need is null) return Refuse("already_resolved");
            if (decision == "revise" && (command is not DecideWorkflowStep { Reply: { Length: > 0 } reply } || string.IsNullOrWhiteSpace(reply))) return Refuse("reply_required");
            if (decision == "revise")
            {
                var text = ((DecideWorkflowStep)command).Reply!;
                if (text.Length > 32768) return Refuse("bad_args");
                // A gate nothing earlier can answer stays open rather than acknowledge and repeat the same question.
                if (WorkflowGateRevision.Target(s, id!) is not { } target) return Refuse("revision_target_undefined");
                return Revise(s, c, run, new(id!, step.Version, step.Attempt, step.Generation), new(need.Id, need.Version), target, text);
            }
        }
        else if (decision == "skip")
        {
            if (step.State != "pending" || step.Required && (!step.Skippable || step.Reason != "condition_false")) return Refuse("required_step");
        }
        else if (decision == "continue")
        {
            if (step.State != "waiting" || step.Reason != "scope_required" || !step.Granted || need is not null) return Refuse("invalid_transition");
            if (step.Session is not null || !CommandStepTransitions.ExecutionsDrained(s, c.EntityId, id, step.Attempt)) return Refuse("executor_quiescence_required");
        }
        else return Refuse("invalid_transition");
        var state = decision switch { "approve" => "succeeded", "deny" => "failed", "skip" => "skipped", _ => "pending" };
        var replyText = (command as DecideWorkflowStep)?.Reply;
        if (replyText is { Length: > 32768 }) return Refuse("bad_args");
        var recovery = decision == "retry" && step.State == "waiting";
        s.Execute("""
            UPDATE engine_step SET state=$state,dispatch_state=CASE WHEN $state='pending' THEN 'pending' ELSE 'settled' END,
              generation=generation+$recovery,
              attempt_in_generation=CASE WHEN $recovery=1 THEN 0 ELSE attempt_in_generation END,
              scope_granted=CASE WHEN $state='pending' THEN 0 ELSE scope_granted END,
              session_id=CASE WHEN $state='pending' THEN NULL ELSE session_id END,
              receipt_id=CASE WHEN $state='pending' THEN NULL ELSE receipt_id END,
              proof_json=CASE WHEN $state='pending' THEN NULL ELSE proof_json END,
              outcome_reason=$reason,ended_ms=CASE WHEN $state='pending' THEN NULL ELSE $now END,deadline_ms=NULL,
              version=version+1,updated_ms=$now WHERE id=$step;
            UPDATE engine_step_attempt SET state=CASE WHEN $state='pending' THEN 'failed' ELSE $state END,
              reason=$reason,gate_state='settled',ended_ms=$now WHERE step_id=$step AND attempt=$attempt AND state IN ('running','waiting');
            """, new("$state", state), new("$recovery", recovery ? 1 : 0), new("$reason", decision == "skip" ? "condition_false" : decision), new("$now", c.NowMs), new("$step", id), new("$attempt", step.Attempt));
        List<EventDraft> events = [StepEvent("step.changed", state)];
        if (need is not null && decision is "approve" or "deny" or "retry")
        {
            s.Execute("""
                UPDATE engine_needs_me SET state='answered',version=version+1,updated_ms=$now,answered_ms=$now,
                  answered_by=$actor,answered_origin_json=$origin,answer_json=$answer WHERE id=$need
                """, new("$now", c.NowMs), new("$actor", c.Actor.Id), new("$origin", NeedTransitions.OriginJson(c)),
                new("$answer", JsonSerializer.Serialize(replyText ?? decision, EngineJson.Default.String)), new("$need", need.Id));
            events.Add(new("needs_me.answered", "needs_me", need.Id, need.Version + 1, false, new(run.Owner, c.EntityId, id, run.Owner, "answered", run.Epoch)));
        }
        s.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'workflow_decision',$now,
              json_object('decision',$decision,'reply',$reply) FROM engine_workflow_history WHERE run_id=$run
            """, new("$id", c.CommandId.ToString("N") + "/decision"), new("$machine", c.Machine), new("$now", c.NowMs), new("$run", c.EntityId),
            new("$step", id), new("$attempt", step.Attempt), new("$generation", step.Generation), new("$decision", decision), new("$reply", replyText));
        events.AddRange(RunTransitions.Recompute(s, c, c.EntityId));
        return Changed(s, c, run with { Version = CurrentVersion(s, c.EntityId), State = s.QueryFirst("SELECT state FROM engine_run WHERE id=$id", r => r.Text(0), new SqlArg("$id", c.EntityId))! }, events, []);
        EventDraft StepEvent(string kind, string state) => new(kind, "step", id!, step.Version + 1, false, new(null, c.EntityId, id, null, state, run.Epoch));
    }
    private static long CurrentVersion(IReadScope s, string id) => s.QueryFirst("SELECT version FROM engine_run WHERE id=$id", r => r.Long(0), new SqlArg("$id", id));
    private static CommandResult CommitRun(IWriteScope s, CommandContext c, Run run) => Changed(s, c, run with { Version = CurrentVersion(s, c.EntityId), State = s.QueryFirst("SELECT state FROM engine_run WHERE id=$id", r => r.Text(0), new SqlArg("$id", c.EntityId))! }, [], []);
}
