using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Scheduling;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>One bounded pass from the existing dispatch callback before workspace/ready preparation.</summary>
public sealed class WorkflowAdvancementPreparation(IAppDatabase db, EngineCommands commands, string machine, TimeProvider time)
{
    public int AdvancePending(CancellationToken ct)
    {
        IReadOnlyList<CommandContext> candidates;
        using (var scope = db.OpenSnapshot(ct))
            candidates = scope.Query("""
                SELECT st.id,st.version,st.generation,COALESCE(st.authority_epoch,0),st.run_id FROM engine_step st JOIN engine_run r ON r.id=st.run_id
                WHERE r.machine=$machine AND st.dispatch_state='redo_pending' AND r.paused=0
                  AND r.state NOT IN ('succeeded','failed','cancelled','cancelling')
                  AND EXISTS(SELECT 1 FROM engine_workflow_history h WHERE h.run_id=r.id AND h.kind='redo_requested'
                    AND h.generation=st.generation AND json_extract(h.safe_body_json,'$.targetStepId')=st.id)
                ORDER BY r.created_ms,r.id,st.id LIMIT 100
                """, r => new CommandContext(DispatchIdentity.Id("workflow-generation-advance/v1", machine, r.Text(4), r.Text(0),
                    r.Long(2).ToString(System.Globalization.CultureInfo.InvariantCulture), r.Long(1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    machine, new(ActorKind.System, "engine.worker"), r.Text(0), r.Long(1), r.Long(3), time.GetUtcNow().ToUnixTimeMilliseconds()),
                new SqlArg("$machine", machine));
        var advanced = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (db.Writer.Write("engine.workflow.advance", scope => commands.AdvanceWorkflowGeneration(scope, candidate)).Applied) advanced++;
        }
        return advanced;
    }
}
