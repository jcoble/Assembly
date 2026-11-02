using Assembly.Application.Common.Database;
using Assembly.Engine.Mcp;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Infrastructure.Engine.Workflows;

/// <summary>Captures the report boundary while the provider waits for its tool result; never waits for release here.</summary>
internal sealed class ReceiptReportPreparation(IAppDatabase db, string machine, Func<McpCaller, string?> currentExecution)
{
    internal async Task<ReceiptReportCapture?> CaptureAsync(McpCaller caller, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CaptureSource? source;
        using (var scope = db.OpenSnapshot(ct)) source = Read(scope, caller);
        if (source is null) return null;
        var executionId = currentExecution(caller);
        if (string.IsNullOrWhiteSpace(executionId)) return null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(TimeSpan.FromSeconds(60));
        var checkout = await new ReceiptCheckout().Capture(source.Path, stop.Token);
        using (var scope = db.OpenSnapshot(ct))
        {
            if (Read(scope, caller) != source || currentExecution(caller) != executionId) return null;
        }
        return new(source.StepId, source.Attempt, source.Generation, caller.SessionId, caller.Epoch,
            source.WorkspaceId, source.WorkspaceVersion, checkout.Head, checkout.Fingerprint, executionId);
    }

    private sealed record CaptureSource(string StepId, int Attempt, int Generation, string WorkspaceId, long WorkspaceVersion, string Path);
    private CaptureSource? Read(IReadScope scope, McpCaller caller) => scope.QueryFirst("""
        SELECT st.id,st.attempt,st.generation,w.id,w.version,w.path FROM engine_step st
        JOIN engine_session se ON se.id=st.session_id JOIN engine_run r ON r.id=st.run_id
        JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt AND a.generation=st.generation
        JOIN engine_workflow_workspace w ON w.id=st.workspace_id
        WHERE se.id=$session AND se.epoch=$epoch AND se.machine=$machine AND se.holder='local'
          AND se.reserved_epoch IS NULL AND se.state NOT IN ('foreign','ended','lost','handing_off')
          AND st.state IN ('running','waiting') AND st.authority_machine=$machine
          AND st.authority_session_id=se.id AND st.authority_epoch=se.epoch
          AND a.session_id=se.id AND a.execution_epoch=se.epoch AND a.workspace_id=w.id AND a.fingerprint IS NOT NULL
          AND w.state='ready' AND w.path IS NOT NULL AND r.state NOT IN ('succeeded','failed','cancelled','cancelling')
        ORDER BY st.updated_ms DESC,st.id LIMIT 1
        """, r => new CaptureSource(r.Text(0), (int)r.Long(1), (int)r.Long(2), r.Text(3), r.Long(4), r.Text(5)),
        new SqlArg("$session", caller.SessionId), new("$epoch", caller.Epoch), new("$machine", machine));
}
