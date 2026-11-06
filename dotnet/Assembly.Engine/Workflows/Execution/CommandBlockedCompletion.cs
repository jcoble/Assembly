using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Workflows.Execution;

public static partial class CommandStepTransitions
{
    /// <summary>Closed extension in CompleteEffect, after reading the actual effect and before updating it.</summary>
    public static string CompletionState(IWriteScope s, CommandContext c, Effect effect, EffectReceipt receipt, string ordinaryState)
    {
        if (receipt.Disposition == EffectDisposition.Unknown) return "unavailable";
        if (receipt.Disposition != EffectDisposition.Unavailable || receipt.Error != "workflow_policy_pending") return ordinaryState;
        var policy = WorkflowExecutionNeeds.Policy(s, effect);
        if (policy == "allow") return "pending"; // An answer may commit before this handler completion.
        if (policy == "closed") return "failed"; // So may a revision that closed the question; the step waits for its redo.
        if (policy != "deny") return "unavailable";
        var i = effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        if (!Current(s, i, c.Machine)) return "failed";
        s.Execute("""
            UPDATE engine_step_attempt SET state='failed',ended_ms=$now,reason='policy_denied',gate_state='settled'
              WHERE step_id=$step AND attempt=$attempt;
            UPDATE engine_step SET state='failed',ended_ms=$now,outcome_reason='policy_denied',version=version+1,updated_ms=$now WHERE id=$step;
            """, new("$now", c.NowMs), new("$step", i.StepId), new("$attempt", i.Attempt));
        EngineJournal.Commit(s, c, c.ExpectedVersion, c.Epoch, RunTransitions.Recompute(s, c, i.RunId), []);
        return "failed";
    }
}
