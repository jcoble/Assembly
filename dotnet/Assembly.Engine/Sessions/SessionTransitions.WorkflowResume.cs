using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Sessions;

internal sealed record WorkflowResumeProof(string StepId, string RunId, string OwnerId, string SessionId,
    long Epoch, int Attempt, int Generation, string WorkspaceId, string Folder,
    string Head, string ReceiptId, string ReceiptHash, string ReleaseId, string Provider,
    string? Model, string NativeId, string AccountContext, bool ManagedCheckout);

public static partial class SessionTransitions
{
    // The release marker is written only after the original capture has been positively
    // released and its immutable receipt accepted. Ending a shell is insufficient.
    internal static WorkflowResumeProof? ReadWorkflowResume(IReadScope scope, string stepId) => scope.QueryFirst("""
        SELECT st.id,st.run_id,run.owner_session_id,worker.id,worker.epoch,
          a.attempt,a.generation,w.id,w.path,w.head,receipt.id,receipt.hash,h.id,
          worker.provider,worker.model,worker.provider_session_id,worker.account_context,worker.managed_checkout
        FROM engine_step st JOIN engine_run run ON run.id=st.run_id
        JOIN engine_session worker ON worker.id=st.session_id
        JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt
        JOIN engine_workflow_workspace w ON w.id=st.workspace_id
        JOIN engine_workflow_receipt receipt ON receipt.id=a.receipt_id
        JOIN engine_workflow_history h ON h.run_id=st.run_id AND h.step_id=st.id
          AND h.attempt=a.attempt AND h.generation=a.generation AND h.kind='workflow_worker_released'
        WHERE st.id=$step AND st.kind='agent' AND st.state='pending' AND st.generation>a.generation
          AND st.attempt_in_generation=0 AND COALESCE(st.dispatch_state,'')<>'redo_pending'
          AND worker.state='ended' AND worker.machine=run.machine AND worker.holder='local'
          AND worker.parent_id=run.owner_session_id AND worker.run_id=run.id AND worker.reserved_epoch IS NULL
          AND worker.provider_session_id IS NOT NULL AND worker.provider_session_id<>''
          AND st.authority_session_id=worker.id AND st.authority_epoch=worker.epoch AND st.authority_machine=worker.machine
          AND a.session_id=worker.id AND a.execution_epoch=worker.epoch AND a.authority_machine=worker.machine
          AND w.state='ready' AND w.run_id=run.id AND w.step_id=st.id AND w.path=worker.folder AND a.workspace_id=w.id
          AND receipt.state='accepted' AND receipt.machine=worker.machine AND receipt.run_id=run.id
          AND receipt.step_id=st.id AND receipt.attempt=a.attempt AND receipt.generation=a.generation
          AND receipt.session_id=worker.id AND receipt.epoch=worker.epoch AND h.machine=worker.machine
          AND json_extract(h.safe_body_json,'$.receiptId')=receipt.id
          AND json_extract(h.safe_body_json,'$.sessionId')=worker.id AND json_extract(h.safe_body_json,'$.epoch')=worker.epoch
          AND json_extract(h.safe_body_json,'$.workspaceId')=w.id AND json_extract(h.safe_body_json,'$.head')=w.head
          AND json_extract(h.safe_body_json,'$.providerSessionId')=worker.provider_session_id
          AND NOT EXISTS(SELECT 1 FROM engine_outbox o WHERE o.session_id=worker.id AND o.state NOT IN ('done','failed','dead'))
          AND NOT EXISTS(SELECT 1 FROM engine_needs_me n WHERE n.session_id=worker.id AND n.state='open')
        ORDER BY h.history_seq DESC LIMIT 1
        """, r => new WorkflowResumeProof(r.Text(0), r.Text(1), r.Text(2), r.Text(3), r.Long(4),
        (int)r.Long(5), (int)r.Long(6), r.Text(7), r.Text(8), r.Text(9), r.Text(10), r.Text(11), r.Text(12),
        r.Text(13), r.IsNull(14) ? null : r.Text(14), r.Text(15), r.Text(16), r.Long(17) != 0), new SqlArg("$step", stepId));

    internal static CommandResult ResumeWorkflowWorker(IWriteScope scope, CommandContext context, WorkflowResumeProof proof, StartSession start)
    {
        var current = Find(scope, proof.SessionId);
        CommandResult Refuse(string reason) => new(false, false, reason, current?.Version ?? 0, 0, proof.Epoch);
        if (context.Actor != new EngineActor(ActorKind.System, "engine.worker") || context.EntityId != proof.SessionId)
            return Refuse("forbidden");
        if (current is null || context.ExpectedVersion != current.Version || context.Epoch != proof.Epoch || ReadWorkflowResume(scope, proof.StepId) != proof)
            return Refuse("version_conflict");
        if (start.Provider != proof.Provider || start.Model != proof.Model || start.Folder != proof.Folder ||
            start.ParentId != proof.OwnerId || start.RunId != proof.RunId || start.ManagedCheckout != proof.ManagedCheckout)
            return Refuse("forbidden");
        var epoch = checked(proof.Epoch + 1);
        var changed = scope.Execute("""
            UPDATE engine_session SET state='starting',activity_state='idle',epoch=$epoch,version=version+1,
              updated_ms=$now,cancel_requested=0,lost_ms=NULL
            WHERE id=$session AND version=$version AND epoch=$oldEpoch AND state='ended' AND machine=$machine
            """, new("$epoch", epoch), new("$now", context.NowMs), new("$session", proof.SessionId),
            new("$version", current.Version), new("$oldEpoch", proof.Epoch), new("$machine", context.Machine));
        if (changed != 1) return Refuse("version_conflict");
        // The retained native ID stays on the same row. The existing host start/send path
        // must resume that ID and refuse a rejected native resume for workflow workers.
        return Commit(scope, context, Find(scope, proof.SessionId)!, "session.changed",
            [new("", "start", proof.SessionId, proof.RunId, JsonSerializer.SerializeToElement(start, EngineJson.Default.StartSession))]);
    }
}
