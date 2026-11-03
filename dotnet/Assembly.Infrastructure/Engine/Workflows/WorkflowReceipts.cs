using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Workflows.Reviews;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Engine.Effects;
using Assembly.Engine.Mcp;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Infrastructure.Engine.Workflows;

public sealed record WorkflowReceiptServices(
    Func<McpCaller, CancellationToken, Task<ReceiptReportCapture?>> Capture,
    IReceiptChecks Checks, IEngineEffects Effects);

public static class WorkflowReceipts
{
    public static WorkflowReceiptServices Create(IAppDatabase db, string machine, SessionRegistry sessions,
        Func<ReceiptEvidenceRequest, CancellationToken, Task<ReceiptEvidenceResult>>? checkEvidence = null) =>
        Create(db, machine, caller => sessions.For(caller.SessionId).CurrentTurn.RunId, (capture, ct) =>
        {
            using var scope = db.OpenSnapshot(ct);
            var fence = new WorkflowExecutionFence(capture.StepId, capture.Generation, capture.Attempt, capture.Epoch);
            var released = capture.ExecutionId is not null && WorkflowExecutionReleases.IsReleased(scope, fence)
                && scope.QueryFirst("""
                    SELECT EXISTS(SELECT 1 FROM engine_step_attempt a JOIN engine_step st ON st.id=a.step_id
                      JOIN engine_workflow_history h ON h.run_id=st.run_id AND h.step_id=a.step_id
                        AND h.attempt=a.attempt AND h.generation=a.generation AND h.kind='execution_released'
                      WHERE a.step_id=$step AND a.attempt=$attempt AND a.generation=$generation
                        AND a.session_id=$session AND a.execution_epoch=$epoch AND a.authority_machine=$machine
                        AND a.workspace_id=$workspace AND h.machine=$machine
                        AND json_extract(h.safe_body_json,'$.sessionId')=$session
                        AND json_extract(h.safe_body_json,'$.epoch')=$epoch
                        AND json_extract(h.safe_body_json,'$.chatRunId')=$execution)
                    """, r => r.Long(0) != 0, new SqlArg("$step", capture.StepId), new("$attempt", capture.Attempt),
                    new("$generation", capture.Generation), new("$session", capture.SessionId), new("$epoch", capture.Epoch),
                    new("$machine", machine), new("$workspace", capture.WorkspaceId), new("$execution", capture.ExecutionId));
            return ValueTask.FromResult(released);
        }, checkEvidence);

    /// <summary>Composes trusted host producers. Missing evidence observations never produce acceptance.</summary>
    public static WorkflowReceiptServices Create(IAppDatabase db, string machine,
        Func<McpCaller, string?> currentExecution,
        Func<ReceiptReportCapture, CancellationToken, ValueTask<bool>> released,
        Func<ReceiptEvidenceRequest, CancellationToken, Task<ReceiptEvidenceResult>>? checkEvidence = null,
        IGit? git = null)
    {
        var report = new ReceiptReportPreparation(db, machine, currentExecution);
        var checks = new ReceiptChecks(db, machine, released, git, checkEvidence);
        return new(report.CaptureAsync, checks, new ReceiptEffects(checks));
    }
}
