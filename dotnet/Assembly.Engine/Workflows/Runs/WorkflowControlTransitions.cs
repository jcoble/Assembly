using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Workflows.Runs;

internal static partial class WorkflowControlTransitions
{
    private sealed record Run(long Version, string Owner, string State, long Epoch, bool Paused, string Machine,
        string? Token, long? Until, long? KeepVersion);
    internal static CommandResult Apply(IWriteScope s, CommandContext c, WorkflowCommand command, Execution.PreparedWorkflowCommandRetry? retry = null)
    {
        if (command is SetWorkflowMute mute) return Mute(s, c, mute);
        var run = s.QueryFirst("""
            SELECT r.version,r.owner_session_id,r.state,se.epoch,r.paused,r.machine,r.keep_token,r.keep_until_ms,r.keep_version
            FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id
            WHERE r.id=$id AND r.definition_hash IS NOT NULL
            """, r => new Run(r.Long(0), r.Text(1), r.Text(2), r.Long(3), r.Long(4) != 0, r.Text(5),
                r.IsNull(6) ? null : r.Text(6), r.IsNull(7) ? null : r.Long(7), r.IsNull(8) ? null : r.Long(8)), new SqlArg("$id", c.EntityId));
        CommandResult Refuse(string error) => new(false, false, error, run?.Version ?? 0, 0, run?.Epoch ?? c.Epoch);
        if (run is null) return Refuse("bad_args");
        if (run.Machine != c.Machine) return Refuse("wrong_machine");
        if (run.Epoch != c.Epoch) return Refuse("stale_epoch");
        if (run.Version != c.ExpectedVersion) return Refuse("version_conflict");
        if (!RunTransitions.CanControl(c, run.Owner)) return Refuse("forbidden");
        var terminal = RunTransitions.IsTerminal(run.State);
        if (command is KeepWorkflowSucceeded or UndoWorkflowKeep)
            return Closure(s, c, run, command);
        if (terminal && command is DecideWorkflowStep decision && s.QueryFirst(
            "SELECT COUNT(*) FROM engine_step st JOIN engine_needs_me n ON n.step_id=st.id WHERE st.id=$step AND st.run_id=$run AND st.kind='gate' AND n.source_kind='workflow_gate' AND n.state='answered'",
            r => r.Long(0), new SqlArg("$step", decision.StepId), new("$run", c.EntityId)) > 0) return Refuse("already_resolved");
        if (terminal || run.State == "cancelling") return Refuse("invalid_transition");
        if (command is PauseWorkflow or ResumeWorkflow)
        {
            var pause = command is PauseWorkflow;
            if (run.Paused == pause) return Refuse("invalid_transition");
            s.Execute("UPDATE engine_run SET paused=$paused WHERE id=$id", new("$paused", pause ? 1 : 0), new("$id", c.EntityId));
            return Changed(s, c, run, [], []);
        }
        return StepControl(s, c, run, command, retry);
    }
    private static CommandResult Mute(IWriteScope s, CommandContext c, SetWorkflowMute mute)
    {
        var version = s.QueryFirst("SELECT version FROM engine_workflow_settings WHERE singleton=1", r => r.Long(0));
        CommandResult Refuse(string error) => new(false, false, error, version, 0, 0);
        if (c.Actor.Kind != ActorKind.Window && c.Actor.Kind != ActorKind.Session) return Refuse("forbidden");
        if (c.Actor.Kind == ActorKind.Session && s.QueryFirst("SELECT COUNT(*) FROM engine_session WHERE id=$id AND parent_id IS NULL AND holder='local' AND machine=$machine AND state NOT IN ('ended','foreign','lost','handing_off')", r => r.Long(0), new SqlArg("$id", c.Actor.Id), new("$machine", c.Machine)) != 1) return Refuse("forbidden");
        if (c.EntityId != "workflow-settings" || mute.UntilMs is < 0) return Refuse("bad_args");
        if (c.Epoch != 0) return Refuse("stale_epoch");
        if (c.ExpectedVersion != version) return Refuse("version_conflict");
        s.Execute("UPDATE engine_workflow_settings SET muted_until=$until,version=version+1 WHERE singleton=1", new SqlArg("$until", mute.UntilMs));
        return EngineJournal.Commit(s, c, version + 1, 0, [new("workflow.settings_changed", "workflow_settings", "workflow-settings", version + 1, false, new(null, null, null, null, null, null))], []);
    }
    private static CommandResult Closure(IWriteScope s, CommandContext c, Run run, WorkflowCommand command)
    {
        CommandResult Refuse(string error) => new(false, false, error, run.Version, 0, run.Epoch);
        if (run.State is not ("failed" or "cancelled")) return Refuse("invalid_transition");
        var keep = command is KeepWorkflowSucceeded;
        if (!keep && (command is not UndoWorkflowKeep undo || !Guid.TryParse(undo.Token, out var token) ||
            token.ToString("D") != run.Token || run.Until <= c.NowMs || run.KeepVersion is null)) return Refuse("invalid_undo");
        if (keep && s.QueryFirst("SELECT COUNT(*) FROM engine_step WHERE run_id=$id AND state IN ('failed','cancelled') AND closed=0", r => r.Long(0), new SqlArg("$id", c.EntityId)) == 0) return Refuse("invalid_transition");
        var events = s.Query("""
            UPDATE engine_step SET closed=$closed,version=version+1,updated_ms=$now
            WHERE run_id=$id AND state IN ('failed','cancelled') AND closed<>$closed RETURNING id,version,state
            """, r => new EventDraft("step.changed", "step", r.Text(0), r.Long(1), false, new(null, c.EntityId, r.Text(0), null, r.Text(2), run.Epoch)),
            new SqlArg("$closed", keep ? 1 : 0), new("$now", c.NowMs), new("$id", c.EntityId));
        s.Execute("UPDATE engine_run SET keep_token=$token,keep_until_ms=$until,keep_version=$version WHERE id=$id",
            new("$token", keep ? c.CommandId.ToString("D") : null), new("$until", keep ? checked(c.NowMs + 5000) : (long?)null),
            new("$version", keep ? run.Version + 1 : (long?)null), new("$id", c.EntityId));
        return Changed(s, c, run, events, []);
    }
    private static CommandResult Changed(IWriteScope s, CommandContext c, Run run, IEnumerable<EventDraft> events, IReadOnlyList<EffectDraft> effects)
    {
        s.Execute("UPDATE engine_run SET version=version+1,updated_ms=$now WHERE id=$id", new("$now", c.NowMs), new("$id", c.EntityId));
        return EngineJournal.Commit(s, c, run.Version + 1, run.Epoch,
            [.. events, new("run.changed", "run", c.EntityId, run.Version + 1, false, new(run.Owner, c.EntityId, null, null, run.State, run.Epoch))], effects);
    }
}
