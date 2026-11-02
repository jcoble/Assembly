using Assembly.Application.Common.Database;
using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Execution;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>Uses the existing worker; process observation is a checked command in its same writer.</summary>
public sealed class CommandStepEffects(IAppDatabase db, EngineCommands commands, string machine, string proofRoot,
    TimeProvider time, Func<string, CancellationToken, Task<string>> fingerprint) : IEngineEffects
{
    private readonly IWorkflowProcess _process = new WorkflowProcess(proofRoot, async (effect, intent, process, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        var accepted = db.Writer.Write("engine.workflow.process_observed", scope =>
        {
            var version = scope.QueryFirst("SELECT version FROM engine_outbox WHERE id=$id", r => r.Long(0), new SqlArg("$id", effect.Id));
            var context = new CommandContext(Guid.NewGuid(), machine, new(ActorKind.System, "workflow.process"),
                effect.Id, version, intent.ExecutionEpoch, time.GetUtcNow().ToUnixTimeMilliseconds());
            return commands.ObserveWorkflowProcess(scope, context,
                new(effect.Id, intent.Generation, intent.Attempt, intent.CommandIndex, intent.ExecutionEpoch, process)).Applied;
        });
        using var snapshot = db.OpenSnapshot(CancellationToken.None);
        return await Task.FromResult(accepted && CommandStepTransitions.CanLaunch(snapshot, intent, machine));
    }, fingerprint);

    public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken ct) => ApplyAsync(effect, false, ct);
    public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken ct) => ApplyAsync(effect, true, ct);

    private async Task<EffectReceipt> ApplyAsync(Effect effect, bool reconcile, CancellationToken ct)
    {
        bool ask;
        var intent = effect.Payload.Deserialize(WorkflowExecutionJson.Default.CommandExecutionIntent)!;
        using (var scope = db.OpenSnapshot(CancellationToken.None))
        {
            if (!reconcile && (ct.IsCancellationRequested || !CommandStepTransitions.CanLaunch(scope, intent, machine)))
                return new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new CommandExecutionReceipt(effect.Id,
                    CommandStepTransitions.Digest(effect.Payload.GetRawText()), null, null, "", "", 0, 0, false, true,
                    "not_started", "", null, NotStarted: true), WorkflowExecutionJson.Default.CommandExecutionReceipt), null);
            ask = !reconcile && WorkflowExecutionNeeds.Policy(scope, effect) != "allow";
        }
        if (ask)
        {
            OpenNeed(effect, "policy");
            return new(EffectDisposition.Unavailable, System.Text.Json.JsonSerializer.SerializeToElement((string?)null,
                EngineJson.Default.String), "workflow_policy_pending");
        }
        var result = await _process.ExecuteAsync(effect, reconcile, ct);
        if (result.Disposition == EffectDisposition.Unknown) OpenNeed(effect, "command_uncertain");
        return result;
    }

    private void OpenNeed(Effect effect, string reason) => db.Writer.Write("engine.workflow.execution_need", scope =>
    {
        var version = scope.QueryFirst("SELECT version FROM engine_outbox WHERE id=$id", r => r.Long(0), new SqlArg("$id", effect.Id));
        var context = new CommandContext(Guid.NewGuid(), machine, new(ActorKind.System, "workflow.process"), effect.Id,
            version, 0, time.GetUtcNow().ToUnixTimeMilliseconds());
        return commands.OpenWorkflowExecutionNeed(scope, context, effect, reason);
    });
}
