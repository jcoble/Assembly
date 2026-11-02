using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Engine.Workflows.Runs;

internal static class WorkflowControlTransitions
{
    internal static CommandResult Apply(IWriteScope s, CommandContext c, WorkflowControl command)
    {
        if (command is DecideWorkflowStep decide)
            return decide.StepId != c.EntityId ? Refuse(c, "bad_args") : WorkflowGateTransitions.Decide(s, c, decide.Decision, decide.Reply);
        if (command is RetryWorkflowStep retry)
        {
            if (retry.StepId != c.EntityId) return Refuse(c, "bad_args");
            var need = s.QueryFirst("""
                SELECT id FROM engine_needs_me WHERE step_id=$step AND source_kind='workflow'
                  AND state='open' AND kind='command_timeout' ORDER BY created_ms,id LIMIT 1
                """, r => r.Text(0), new SqlArg("$step", c.EntityId));
            return need is null ? Refuse(c, "retry_not_available") : CommandStepTransitions.AuthorizeTimeoutRetry(s, c, need);
        }
        var run = s.QueryFirst("""
            SELECT r.version,r.state,r.paused,r.owner_session_id,o.epoch FROM engine_run r
            JOIN engine_session o ON o.id=r.owner_session_id
            WHERE r.id=$run AND r.definition_hash IS NOT NULL AND r.machine=$machine AND o.machine=$machine
              AND o.parent_id IS NULL AND o.holder='local' AND o.reserved_epoch IS NULL
              AND o.cancel_requested=0 AND o.state IN ('idle','running','needs_me')
            """, r => new { Version = r.Long(0), State = r.Text(1), Paused = r.Long(2) != 0, Owner = r.Text(3), Epoch = r.Long(4) },
            new SqlArg("$run", c.EntityId), new("$machine", c.Machine));
        if (run is null) return Refuse(c, "workflow_not_available");
        if (!RunTransitions.CanControl(c, run.Owner)) return Refuse(c, "forbidden");
        if (c.ExpectedVersion != run.Version) return Refuse(c, "version_conflict");
        if (c.Epoch != run.Epoch) return Refuse(c, "stale_epoch");
        if (RunTransitions.IsTerminal(run.State) || run.State == "cancelling") return Refuse(c, "invalid_transition");
        var paused = command is PauseWorkflow;
        if (command is not (PauseWorkflow or ResumeWorkflow)) return Refuse(c, "unsupported_command");
        if (run.Paused == paused) return Refuse(c, "invalid_transition");
        s.Execute("UPDATE engine_run SET paused=$paused,version=version+1,updated_ms=$now WHERE id=$run",
            new("$paused", paused ? 1 : 0), new("$now", c.NowMs), new("$run", c.EntityId));
        return EngineJournal.Commit(s, c, run.Version + 1, run.Epoch,
            [new(paused ? "workflow.paused" : "workflow.resumed", "run", c.EntityId, run.Version + 1, false,
                new(run.Owner, c.EntityId, null, null, run.State, run.Epoch))], []);
    }

    internal static CommandResult Refuse(CommandContext c, string error) => new(false, false, error, c.ExpectedVersion, 0, c.Epoch);
}
