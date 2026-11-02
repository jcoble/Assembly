using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Runs;
using Assembly.Engine.Workflows.Workspaces;
using Assembly.Infrastructure.Git;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>The existing effect worker owns both setup preparation and the subsequent workspace I/O.</summary>
public sealed class WorkspaceEffects(IAppDatabase db, EngineCommands commands, string machine, string managedRoot,
    string proofRoot, IWorkflowWorkspace workspace, TimeProvider time, IGit? git = null) : IEngineEffects
{
    private readonly IGit _git = git ?? new GitCli();
    public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken ct) => ApplyAsync(effect, false, ct);
    public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken ct) => ApplyAsync(effect, true, ct);

    private async Task<EffectReceipt> ApplyAsync(Effect effect, bool reconcile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (var scope = db.OpenSnapshot(ct))
            if (!Current(scope, effect)) return Unknown();
        if (effect.Kind == "workflow_workspace") return await workspace.PrepareAsync(effect, reconcile, ct);
        if (effect.Kind != "workflow_setup") throw new CallFailed("invalid_workspace_effect");
        var setup = effect.Payload.Deserialize(EngineJson.Default.WorkflowSetupIntent)!;
        try
        {
            string root;
            using (var scope = db.OpenSnapshot(ct))
            {
                var existing = Allocation(scope, setup);
                if (existing is not null) return Setup(new(existing));
                root = scope.QueryFirst("SELECT project_root FROM engine_run WHERE id=$run", r => r.Text(0), new SqlArg("$run", setup.RunId))!;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(TimeSpan.FromSeconds(60));
            List<string> values = [];
            await foreach (var value in _git.ReadAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], null, stop.Token)) values.Add(value);
            var head = string.Concat(values).TrimEnd('\r', '\n');
            Assembly.Engine.Workflows.PreparedWorkflowCommand prepared;
            CommandContext context;
            using (var scope = db.OpenSnapshot(ct))
            {
                if (!Current(scope, effect)) return Unknown();
                var current = scope.QueryFirst("""
                    SELECT r.version,se.epoch FROM engine_run r JOIN engine_session se ON se.id=r.owner_session_id WHERE r.id=$run
                    """, r => (Version: r.Long(0), Epoch: r.Long(1)), new SqlArg("$run", setup.RunId));
                context = new(Guid.NewGuid(), machine, new(ActorKind.System, "workflow.workspace"), setup.RunId,
                    current.Version, current.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
                prepared = WorkspacePreparation.Prepare(scope, context, new(null, 0, Guid.NewGuid().ToString("N"), head, "new"), managedRoot, proofRoot, ct);
            }
            ct.ThrowIfCancellationRequested();
            var allocation = db.Writer.Write("engine.workflow.setup", scope =>
            {
                if (!Current(scope, effect)) return null;
                var result = commands.ApplyPreparedWorkflow(scope, context, prepared);
                if (!result.Applied) throw new CallFailed(result.Error ?? "workflow_setup_refused");
                return Allocation(scope, setup);
            });
            return allocation is null ? Unknown() : Setup(new(allocation));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Setup(new(null, "workspace_setup_timeout")); }
        catch (Exception error) when (error is CallFailed or IOException or UnauthorizedAccessException or ArgumentException)
        { return Setup(new(null, error.Message)); }
    }

    private bool Current(IReadScope scope, Effect effect) => effect.SessionId is null && effect.RunId is not null && scope.QueryFirst("""
        SELECT COUNT(*) FROM engine_outbox o JOIN engine_run r ON r.id=o.run_id JOIN engine_session se ON se.id=r.owner_session_id
        WHERE o.id=$id AND o.kind=$kind AND o.run_id=$run AND o.machine=$machine AND o.state='inflight' AND o.attempt=$attempt
          AND o.payload_json=$payload AND r.machine=$machine AND se.machine=$machine AND se.holder='local'
          AND se.reserved_epoch IS NULL AND se.state NOT IN ('foreign','ended','handing_off','lost')
          AND r.state NOT IN ('succeeded','failed','cancelled','cancelling')
        """, r => r.Long(0), new SqlArg("$id", effect.Id), new("$kind", effect.Kind), new("$run", effect.RunId), new("$machine", machine),
        new("$attempt", effect.Attempt), new("$payload", effect.Payload.GetRawText())) == 1;
    private static string? Allocation(IReadScope scope, WorkflowSetupIntent setup) => scope.QueryFirst("""
        SELECT w.allocation_id FROM engine_workflow_workspace w JOIN engine_run r ON r.id=w.run_id
        WHERE w.run_id=$run AND w.step_id IS NULL AND r.definition_hash=$hash ORDER BY w.created_ms DESC LIMIT 1
        """, r => r.Text(0), new SqlArg("$run", setup.RunId), new("$hash", setup.DefinitionHash));
    private static EffectReceipt Setup(WorkspaceSetupData data) => new(EffectDisposition.Confirmed,
        JsonSerializer.SerializeToElement(data, EngineJson.Default.WorkspaceSetupData), null);
    private static EffectReceipt Unknown() => new(EffectDisposition.Unknown,
        JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), "workspace_authority_changed");
}
