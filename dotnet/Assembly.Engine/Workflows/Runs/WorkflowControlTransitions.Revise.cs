using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Workflows.Reviews;

namespace Assembly.Engine.Workflows.Runs;

internal static partial class WorkflowControlTransitions
{
    private sealed record GateStep(string Id, long Version, int Attempt, int Generation);
    private sealed record GateNeed(string Id, long Version);

    /// <summary>Answers the gate with the owner's text and sends the work back through the existing redo: the target and
    /// everything after it run again with that text as their findings, and the gate opens anew when they finish.</summary>
    private static CommandResult Revise(IWriteScope s, CommandContext c, Run run, GateStep gate, GateNeed need, string target, string text)
    {
        s.Execute("""
            UPDATE engine_needs_me SET state='answered',version=version+1,updated_ms=$now,answered_ms=$now,
              answered_by=$actor,answered_origin_json=$origin,answer_json=$answer WHERE id=$need
            """, new("$now", c.NowMs), new("$actor", c.Actor.Id), new("$origin", NeedTransitions.OriginJson(c)),
            new("$answer", JsonSerializer.Serialize(text, EngineJson.Default.String)), new("$need", need.Id));
        s.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'workflow_decision',$now,
              json_object('decision','revise','reply',$reply) FROM engine_workflow_history WHERE run_id=$run
            """, new("$id", c.CommandId.ToString("N") + "/decision"), new("$machine", c.Machine), new("$now", c.NowMs), new("$run", c.EntityId),
            new("$step", gate.Id), new("$attempt", gate.Attempt), new("$generation", gate.Generation), new("$reply", text));
        var redo = ReviewTransitions.GateRevised(s, c with { EntityId = gate.Id, ExpectedVersion = gate.Version }, c.EntityId, gate.Attempt, target, text);
        if (!redo.Applied) throw new InvalidOperationException(redo.Error ?? "gate_revision_refused");
        s.Execute("UPDATE engine_step_attempt SET reason='revise',gate_state='settled' WHERE step_id=$step AND attempt=$attempt",
            new("$step", gate.Id), new("$attempt", gate.Attempt));
        List<EventDraft> events = [new("needs_me.answered", "needs_me", need.Id, need.Version + 1, false,
            new(run.Owner, c.EntityId, gate.Id, run.Owner, "answered", run.Epoch))];
        events.AddRange(RunTransitions.Recompute(s, c, c.EntityId));
        return Changed(s, c, run with
        {
            Version = CurrentVersion(s, c.EntityId),
            State = s.QueryFirst("SELECT state FROM engine_run WHERE id=$id", r => r.Text(0), new SqlArg("$id", c.EntityId))!
        }, events, []);
    }
}
