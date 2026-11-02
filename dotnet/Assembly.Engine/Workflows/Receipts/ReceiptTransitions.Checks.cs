using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;
using Assembly.Engine.Sessions;
using Assembly.Engine.Messages;

namespace Assembly.Engine.Workflows.Receipts;

public static partial class ReceiptTransitions
{
    public static bool ValidConfirmation(IReadScope scope, Effect effect, EffectReceipt receipt)
    {
        var prepared = receipt.WorkflowReceiptConfirmation;
        if (prepared is null || effect.Kind != "workflow_receipt" || effect.SessionId is not null) return false;
        var body = prepared.Body;
        ReceiptVerificationIntent? intent;
        try { intent = effect.Payload.Deserialize(ReceiptPreparationJson.Default.ReceiptVerificationIntent); }
        catch (JsonException) { return false; }
        if (intent is null || intent != body.Intent || effect.Id != body.EffectId ||
            effect.Attempt != body.EffectAttempt || effect.RunId != body.RunId || body.ReceiptId != body.Check.ReceiptId ||
            body.Check.Accepted == (body.Check.Error is not null)) return false;
        var row = scope.QueryFirst("""
            SELECT body_json,state,hash,attempt,generation,run_id,step_id FROM engine_workflow_receipt WHERE id=$id
            """, r => new
        {
            Json = r.Text(0),
            State = r.Text(1),
            Hash = r.Text(2),
            Attempt = r.Long(3),
            Generation = r.Long(4),
            Run = r.Text(5),
            Step = r.Text(6)
        }, new SqlArg("$id", body.ReceiptId));
        if (row is null || row.State != "pending" || row.Hash != intent.Hash || row.Run != effect.RunId ||
            row.Step != intent.StepId || row.Attempt != body.Check.Attempt || row.Generation != body.Check.Generation) return false;
        var original = JsonSerializer.Deserialize(row.Json, ReceiptPreparationJson.Default.ReceiptBody);
        if (original is null || scope.QueryFirst("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id=$id AND json_extract(result_json,'$.applied')=1",
            r => r.Long(0), new SqlArg("$id", original.Context.CommandId.ToString("N"))) != 1) return false;
        return !body.Check.Accepted ||
            body.ReleasedExecution == original.Capture && Oid(body.Check.Head) && body.Check.Head == original.Capture.Head &&
            body.EndFingerprint == original.Capture.Fingerprint && Hash(body.EndFingerprint) && EvidenceMatches(original, body);
    }

    private static bool EvidenceMatches(ReceiptBody original, ReceiptConfirmationBody checkedBody)
    {
        var required = new List<string>(original.Report.ProofUrls);
        var receipt = original.Report.Receipt!.Value;
        if (receipt.TryGetProperty("commits", out var commits))
        {
            var tests = receipt.GetProperty("tests_added").EnumerateArray().Select(test => test.GetString()!).ToArray();
            if (commits.GetArrayLength() > 0 || tests.Length > 0)
            {
                var verified = checkedBody.BuildVerification;
                if (verified is null || verified.TestedHead != checkedBody.Check.Head || !verified.Tests.SequenceEqual(tests)) return false;
                if (verified.Result == "tests_verified")
                {
                    if (tests.Length == 0 || verified.Reason is not null) return false;
                }
                else if (verified.Result != "owner_review" || string.IsNullOrWhiteSpace(verified.Reason) ||
                    !receipt.GetProperty("summary").GetString()!.Contains(verified.Reason, StringComparison.Ordinal)) return false;
            }
        }
        if (receipt.TryGetProperty("url", out var document)) required.Add(document.GetString()!);
        if (receipt.TryGetProperty("proofUrls", out var urls)) required.AddRange(urls.EnumerateArray().Select(u => u.GetString()!));
        if (receipt.TryGetProperty("findings", out var findings))
            foreach (var finding in findings.EnumerateArray())
                if (finding.TryGetProperty("path_line", out var path) && Uri.TryCreate(path.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == "https")
                    required.Add(path.GetString()!);
        if (receipt.TryGetProperty("checks", out var checks))
            foreach (var check in checks.EnumerateArray())
            {
                var url = check.GetProperty("receiptUrl").GetString()!;
                required.Add(url);
                if (!checkedBody.Checks.Any(observed => observed.Name == check.GetProperty("name").GetString() &&
                    observed.Result == check.GetProperty("result").GetString() && observed.ReceiptUrl == url && observed.TestedHead == checkedBody.Check.Head))
                    return false;
            }
        return required.Count > 0 && required.All(url => checkedBody.Evidence.Any(observed =>
            observed.Url == url && observed.Result == "reachable" && observed.TestedHead == checkedBody.Check.Head));
    }

    public static void Confirm(IWriteScope scope, CommandContext context, Effect effect, EffectReceipt receipt,
        Func<IWriteScope, CommandContext, string, CommandResult> completeChecked,
        Func<IWriteScope, CommandContext, string, CommandResult> reviewFinished, MessageExpiryPolicy? expiryPolicy = null)
    {
        if (!ValidConfirmation(scope, effect, receipt)) throw new InvalidOperationException("Unbound workflow receipt confirmation.");
        var prepared = receipt.WorkflowReceiptConfirmation!;
        var body = prepared.Body; var intent = body.Intent; var check = body.Check;
        var row = scope.QueryFirst("SELECT kind,body_json,version FROM engine_workflow_receipt WHERE id=$id",
            r => (Kind: r.Text(0), Json: r.Text(1), Version: r.Long(2)), new SqlArg("$id", intent.ReceiptId));
        var original = JsonSerializer.Deserialize(row.Json, ReceiptPreparationJson.Default.ReceiptBody)!;
        var current = Read(scope, intent.StepId);
        var late = current is null || current.Attempt != check.Attempt || current.Generation != check.Generation ||
            current.Epoch != original.Capture.Epoch || current.SessionId != original.Capture.SessionId ||
            current.AuthorityEpoch != original.Capture.Epoch || current.AuthoritySession != original.Capture.SessionId ||
            current.WorkspaceId != original.Capture.WorkspaceId || current.WorkspaceVersion != original.Capture.WorkspaceVersion || current.WorkspaceState != "ready" ||
            RunTransitions.IsTerminal(current.RunState) || current.RunState == "cancelling" ||
            current.State is "succeeded" or "failed" or "skipped" or "cancelled" ||
            current.SessionState is "lost" or "ended" or "handing_off" ||
            current.DispatchState == "redo_pending" && row.Kind != "review";
        CheckedHistory(scope, context, prepared);
        if (late)
        {
            EngineJournal.Commit(scope, context, current?.Version ?? 0, original.Capture.Epoch,
                [new("step.late", "step", intent.StepId, current?.Version ?? 0, true,
                    new(original.Capture.SessionId, effect.RunId, intent.StepId, null, current?.State, original.Capture.Epoch))], [], late: true);
            return;
        }
        scope.Execute("""
            UPDATE engine_workflow_receipt SET state=$state,observed_head=$head,checked_ms=$now,error=$error,
              version=version+1,updated_ms=$now WHERE id=$id
            """, new("$state", check.Accepted ? "accepted" : "rejected"), new("$head", check.Head), new("$now", context.NowMs),
            new("$error", check.Error), new("$id", intent.ReceiptId));
        EngineJournal.Commit(scope, context, row.Version + 1, original.Capture.Epoch,
            [new("workflow.receipt_changed", "receipt", intent.ReceiptId, row.Version + 1, false,
                new(original.Capture.SessionId, effect.RunId, intent.StepId, null, check.Accepted ? "accepted" : "rejected", original.Capture.Epoch))], []);
        if (!check.Accepted) return;
        AdvanceCheckedHead(scope, context, original.Capture, body.Check.Head!, row.Kind, effect.RunId!);
        var target = context with { EntityId = intent.StepId, ExpectedVersion = current!.Version, Epoch = current.Epoch };
        var reviewedFailure = row.Kind == "review" &&
            original.Report.Receipt!.Value.GetProperty("verdict").GetString() == "fail";
        // A valid review failure retains its findings and fences redo before core run recomputation can fail the run.
        var result = reviewedFailure ? reviewFinished(scope, target, intent.ReceiptId)
            : row.Kind != "review" && original.Report.State == "failed"
                ? StepTransitions.Apply(scope, target, new SetStepState("failed", original.Report.Attempt,
                    original.Report.ProofUrls, original.Report.Summary, false))
                : completeChecked(scope, target, intent.ReceiptId);
        if (!result.Applied) throw new InvalidOperationException("Checked workflow completion refused: " + result.Error);
        EndReleasedWorker(scope, target, original.Capture, intent.ReceiptId, expiryPolicy);
    }

    private static void AdvanceCheckedHead(IWriteScope scope, CommandContext context, ReceiptReportCapture capture, string head, string kind, string runId)
    {
        var workspace = scope.QueryFirst("""
            UPDATE engine_workflow_workspace SET head=$head,version=version+1,updated_ms=$now
            WHERE id=$workspace AND version=$version AND state='ready' AND head IS NOT $head RETURNING version
            """, r => (long?)r.Long(0), new SqlArg("$head", head), new("$now", context.NowMs),
            new("$workspace", capture.WorkspaceId), new("$version", capture.WorkspaceVersion));
        if (workspace is { } workspaceVersion)
            EngineJournal.Commit(scope, context, workspaceVersion, capture.Epoch,
                [new("workspace.changed", "workspace", capture.WorkspaceId, workspaceVersion, false,
                    new(capture.SessionId, runId, capture.StepId, null, "ready", capture.Epoch))], []);
        if (kind != "build") return;
        var run = scope.QueryFirst("""
            UPDATE engine_run SET work_head=$head,version=version+1,updated_ms=$now WHERE id=$run AND work_head IS NOT $head
            RETURNING version,owner_session_id,state
            """, r => (Version: r.Long(0), Owner: r.Text(1), State: r.Text(2)), new SqlArg("$head", head),
            new("$now", context.NowMs), new("$run", runId));
        if (run.Owner is null) return;
        var epoch = scope.QueryFirst("SELECT epoch FROM engine_session WHERE id=$id", r => r.Long(0), new SqlArg("$id", run.Owner));
        EngineJournal.Commit(scope, context, run.Version, epoch,
            [new("run.changed", "run", runId, run.Version, false, new(run.Owner, runId, null, null, run.State, epoch))], []);
    }

    private static void EndReleasedWorker(IWriteScope scope, CommandContext context, ReceiptReportCapture capture, string receiptId, MessageExpiryPolicy? expiryPolicy)
    {
        var worker = scope.QueryFirst("""
            SELECT se.version,se.state,se.provider_session_id FROM engine_session se
            JOIN engine_step st ON st.session_id=se.id JOIN engine_step_attempt a ON a.step_id=st.id
              AND a.attempt=st.attempt AND a.generation=st.generation
            WHERE st.id=$step AND st.attempt=$attempt AND st.generation=$generation
              AND st.authority_session_id=se.id AND st.authority_epoch=se.epoch
              AND a.session_id=se.id AND a.execution_epoch=se.epoch AND a.authority_machine=$machine
              AND se.id=$session AND se.epoch=$epoch AND se.machine=$machine AND se.holder='local'
              AND se.reserved_epoch IS NULL
            """, r => (Version: r.Long(0), State: r.Text(1), Native: r.IsNull(2) ? null : r.Text(2)), new SqlArg("$step", capture.StepId),
            new("$attempt", capture.Attempt), new("$generation", capture.Generation), new("$session", capture.SessionId),
            new("$epoch", capture.Epoch), new("$machine", context.Machine));
        if (worker.State is null) throw new InvalidOperationException("Released workflow worker binding changed.");
        if (worker.State != "ended")
        {
            var ended = SessionTransitions.Apply(scope, context with { EntityId = capture.SessionId, ExpectedVersion = worker.Version },
                new SetSessionState("ended", null, "workflow_receipt_checked"), 0, 0, expiryPolicy: expiryPolicy);
            if (!ended.Applied) throw new InvalidOperationException("Released workflow worker finalization refused: " + ended.Error);
        }
        var released = new WorkflowWorkerReleased(receiptId, capture.StepId, capture.Attempt, capture.Generation,
            capture.SessionId, capture.Epoch, capture.ExecutionId!, capture.WorkspaceId, capture.Head, capture.Fingerprint, worker.Native);
        scope.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            SELECT $id,1,$machine,$now,$now,run_id,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'workflow_worker_released',$now,$body
            FROM engine_workflow_history WHERE run_id=(SELECT run_id FROM engine_step WHERE id=$step)
            """, new("$id", context.CommandId.ToString("N") + "/worker_released"), new("$machine", context.Machine),
            new("$now", context.NowMs), new("$step", capture.StepId), new("$attempt", capture.Attempt), new("$generation", capture.Generation),
            new("$body", JsonSerializer.Serialize(released, ReceiptPreparationJson.Default.WorkflowWorkerReleased)));
        var runId = scope.QueryFirst("SELECT run_id FROM engine_step WHERE id=$step", r => r.Text(0), new SqlArg("$step", capture.StepId));
        var events = RunTransitions.Recompute(scope, context, runId!);
        if (events.Count > 0) EngineJournal.Commit(scope, context, context.ExpectedVersion, capture.Epoch, events, []);
    }

    private static void CheckedHistory(IWriteScope scope, CommandContext context, PreparedReceiptConfirmation prepared)
    {
        var body = prepared.Body;
        scope.Execute("""
            INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
            SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,'receipt_checked',$now,$body
            FROM engine_workflow_history WHERE run_id=$run
            """, new("$id", context.CommandId.ToString("N") + "/receipt_checked"), new("$machine", context.Machine),
            new("$now", context.NowMs), new("$run", body.RunId), new("$step", body.Intent.StepId),
            new("$attempt", body.Check.Attempt), new("$generation", body.Check.Generation), new("$body", prepared.BodyJson));
    }
}

