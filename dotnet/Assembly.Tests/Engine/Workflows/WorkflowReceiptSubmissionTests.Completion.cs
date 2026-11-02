using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Tests.Engine.Workflows;

public sealed partial class WorkflowReceiptSubmissionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bound_effect_completion_releases_only_accepted_workers(bool accepted)
    {
        using var f = new Fixture(); var submitted = f.Prepare();
        Assert.True(f.Apply(submitted).Applied);
        var priorWorkerState = f.Core.State("engine_session", "worker");
        var id = f.Core.Text("SELECT id FROM engine_outbox WHERE kind='workflow_receipt'")!;
        Assert.True(f.Core.Apply(new ClaimEffect(), id, version: 0, actor: new(ActorKind.System, "worker")).Applied);
        var effect = EffectTransitions.Read(f.Core.Db, id, default)!;
        var original = submitted.Receipt!;
        var confirmation = WorkflowPreparation.ReceiptConfirmation(effect,
            new(original.ReceiptId, 1, 0, accepted, original.Capture.Head, accepted ? null : "evidence_rejected"),
            original.Capture.Fingerprint, [],
            [new("https://evidence.invalid/proof", "reachable", 1000, null, null, original.Capture.Head)], default,
            releasedExecution: original.Capture);
        var result = f.Core.Apply(new CompleteEffect(confirmation), id,
            version: f.Core.Number("SELECT version FROM engine_outbox WHERE kind='workflow_receipt'"),
            actor: new(ActorKind.System, "worker"));
        Assert.True(result.Applied, result.Error);
        Assert.Equal(accepted ? "accepted" : "rejected", f.Core.Text("SELECT state FROM engine_workflow_receipt"));
        Assert.Equal(accepted ? "succeeded" : "waiting", f.Core.State("engine_step", "step"));
        Assert.Equal(accepted ? "ended" : priorWorkerState, f.Core.State("engine_session", "worker"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='receipt_checked'"));
        Assert.Equal(accepted ? 1 : 0, f.Core.Number("SELECT COUNT(*) FROM engine_workflow_history WHERE kind='workflow_worker_released'"));
        Assert.Equal("done", f.Core.State("engine_outbox", id));
    }
}
