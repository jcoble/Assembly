using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Workflows.Scheduling;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Workspaces;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>Called by the existing worker outside its writer. No provider or Git I/O runs under a writer.</summary>
public sealed class WorkflowDispatchPreparation(IAppDatabase db, EngineCommands commands, string machine, TimeProvider time, string managedRoot, string proofRoot)
{
    private readonly ReceiptCheckout _checkout = new();

    public async Task<IReadOnlyList<PreparedWorkflowDispatch>> PrepareAsync(CancellationToken ct)
    {
        PrepareMissingWorkspaces(ct);
        IReadOnlyList<string> ready;
        using (var scope = db.OpenSnapshot(ct))
            ready = WorkflowSchedule.Ready(scope, machine, time.GetUtcNow().ToUnixTimeMilliseconds(), 100);
        List<PreparedWorkflowDispatch> prepared = [];
        foreach (var step in ready)
        {
            ct.ThrowIfCancellationRequested();
            var workspace = db.QueryFirst("""
                SELECT w.path,w.id,w.version FROM engine_step st JOIN engine_workflow_workspace w ON w.id=st.workspace_id
                WHERE st.id=$step AND st.machine=$machine AND w.state='ready'
                """, r => new { Path = r.Text(0), Id = r.Text(1), Version = r.Long(2) }, ct, new SqlArg("$step", step), new("$machine", machine));
            if (workspace is null) continue;
            var checkout = await _checkout.Capture(workspace.Path, ct);
            using var scope = db.OpenSnapshot(ct);
            if (scope.QueryFirst("SELECT COUNT(*) FROM engine_step st JOIN engine_workflow_workspace w ON w.id=st.workspace_id WHERE st.id=$step AND w.id=$id AND w.version=$version AND w.path=$path", r => r.Long(0), new SqlArg("$step", step), new("$id", workspace.Id), new("$version", workspace.Version), new("$path", workspace.Path)) != 1) continue;
            var candidate = commands.PrepareWorkflowDispatch(scope, step, time.GetUtcNow().ToUnixTimeMilliseconds(), checkout.Head, checkout.Fingerprint);
            if (candidate is not null) prepared.Add(candidate);
        }
        return prepared;
    }

    private void PrepareMissingWorkspaces(CancellationToken ct)
    {
        using var snapshot = db.OpenSnapshot(ct);
        var candidates = snapshot.Query($"""
            SELECT st.id,st.run_id,st.generation,run.version,owner.epoch,run.work_head,
              json_extract(source.snapshot_json,'$.snapshot.definition.steps.' || st.definition_key || '.workspace')
            FROM engine_step st JOIN engine_run run ON run.id=st.run_id
            JOIN engine_session owner ON owner.id=run.owner_session_id
            JOIN engine_workflow_snapshot source ON source.run_id=run.id
            WHERE run.machine=$machine AND run.state IN ('queued','running','needs_me')
              AND run.paused=0 AND run.setup_state='ready' AND st.state='pending' AND st.workspace_id IS NULL
              AND st.kind IN ('agent','review') AND COALESCE(st.dispatch_state,'')<>'redo_pending' AND owner.machine=$machine AND owner.holder='local'
              AND owner.state IN ('idle','running','needs_me') AND owner.cancel_requested=0 AND owner.reserved_epoch IS NULL
              AND NOT EXISTS ({WorkflowSchedule.UnsettledDependency})
            ORDER BY run.created_ms,run.id,st.rowid LIMIT 100
            """, r => new { Step = r.Text(0), Run = r.Text(1), Generation = (int)r.Long(2), Version = r.Long(3), Epoch = r.Long(4), Head = r.Text(5), Mode = r.Text(6) }, new SqlArg("$machine", machine));
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var allocation = DispatchIdentity.Id("workflow-dispatch-workspace/v1", machine, candidate.Run, candidate.Step, candidate.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToString("N");
            var context = new CommandContext(Guid.NewGuid(), machine, new(ActorKind.System, "engine.worker"), candidate.Run, candidate.Version, candidate.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
            var prepared = WorkspacePreparation.Prepare(snapshot, context, new PrepareWorkspace(candidate.Step, candidate.Generation, allocation, candidate.Head, candidate.Mode), managedRoot, proofRoot, ct);
            db.Writer.Write("engine.workflow.workspace", scope => commands.ApplyPreparedWorkflow(scope, context, prepared));
        }
    }
}
