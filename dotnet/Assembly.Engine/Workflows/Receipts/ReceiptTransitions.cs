using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Runs;

namespace Assembly.Engine.Workflows.Receipts;

public static partial class ReceiptTransitions
{
    internal static CommandResult Submit(IWriteScope s, CommandContext c, PreparedWorkflowCommand prepared)
    {
        if (prepared.ReceiptReplay) throw new InvalidOperationException("Receipt replay transition must be unreachable.");
        var body = prepared.Receipt!; var report = body.Report; var capture = body.Capture;
        CommandResult Refuse(string error, long? version = null) => new(false, false, error, version ?? c.ExpectedVersion, 0, c.Epoch);
        if (body.Context != c with { NowMs = 0 } || capture.StepId != c.EntityId || c.Actor.Kind != ActorKind.Session ||
            c.Actor.Id != capture.SessionId) return Refuse("forbidden");
        var step = Read(s, c.EntityId);
        if (step is null || step.Kind is null || step.Machine != c.Machine) return Refuse("wrong_machine");
        if (step.SessionId != c.Actor.Id || step.AuthoritySession != c.Actor.Id || step.Holder != "local" || step.SessionMachine != c.Machine)
            return Refuse("forbidden", step.Version);
        if (c.Epoch != step.Epoch || capture.Epoch != step.Epoch || step.AuthorityEpoch != step.Epoch) return Refuse("stale_epoch", step.Version);
        if (report.Attempt != step.Attempt || report.Generation != step.Generation || capture.Attempt != step.Attempt || capture.Generation != step.Generation)
            return Refuse("wrong_attempt", step.Version);
        if (step.DispatchState == "redo_pending" && step.Kind != "review")
        {
            History(s, c, step.RunId, report.Attempt, report.Generation, "receipt_late", body.ReceiptId);
            return EngineJournal.Commit(s, c, step.Version, step.Epoch,
                [new("step.late", "step", c.EntityId, step.Version, true, new(c.Actor.Id, step.RunId, c.EntityId, null, step.State, step.Epoch))], [], late: true);
        }
        var existing = s.QueryFirst("""
            SELECT id,hash,state FROM engine_workflow_receipt WHERE step_id=$step AND attempt=$attempt AND generation=$generation
              AND (state IN ('pending','accepted') OR hash=$hash) ORDER BY created_ms,id LIMIT 1
            """, r => (Id: r.Text(0), Hash: r.Text(1), State: r.Text(2)), new SqlArg("$step", c.EntityId),
            new("$attempt", report.Attempt), new("$generation", report.Generation), new("$hash", body.ReportHash));
        if (existing.Id is not null) return existing.Hash == body.ReportHash ? new(true, false, null, step.Version, 0, step.Epoch) : Refuse("report_conflict", step.Version);
        if (c.ExpectedVersion != step.Version) return Refuse("version_conflict", step.Version);
        if (RunTransitions.IsTerminal(step.RunState) || step.RunState == "cancelling" || step.State is not ("running" or "waiting") ||
            step.SessionState is "lost" or "ended" or "handing_off") return Refuse("invalid_transition", step.Version);
        if (report.State is not ("succeeded" or "failed") || string.IsNullOrWhiteSpace(report.Summary)) return Refuse("bad_args", step.Version);
        if (report.State == "failed" && report.Receipt is null) return StepTransitions.Apply(s, c, new SetStepState("failed", report.Attempt, report.ProofUrls, report.Summary, false));
        if (report.Receipt is null) return Refuse("receipt_required", step.Version);
        var shape = ReceiptValidation.Shape(step.Kind, report.Receipt.Value);
        if (shape is not null) return Refuse("receipt." + shape, step.Version);
        if (report.ProofUrls.Count == 0 || report.ProofUrls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
            return Refuse("proof_required", step.Version);
        if (capture.WorkspaceId != step.WorkspaceId || capture.WorkspaceVersion != step.WorkspaceVersion || step.WorkspaceState != "ready" ||
            string.IsNullOrWhiteSpace(capture.ExecutionId) || !Oid(capture.Head) || !Hash(capture.Fingerprint) || !Hash(step.StartFingerprint)) return Refuse("workspace_not_ready", step.Version);
        s.Execute("""
            INSERT INTO engine_workflow_receipt(id,version,machine,created_ms,updated_ms,run_id,step_id,attempt,generation,session_id,epoch,hash,kind,body_json,state)
            VALUES($id,1,$machine,$now,$now,$run,$step,$attempt,$generation,$session,$epoch,$hash,$kind,$body,'pending');
            UPDATE engine_step SET state='waiting',receipt_id=$id,report_requested=1,version=version+1,updated_ms=$now WHERE id=$step;
            UPDATE engine_step_attempt SET receipt_id=$id WHERE step_id=$step AND attempt=$attempt AND generation=$generation;
            """, new("$id", body.ReceiptId), new("$machine", c.Machine), new("$now", c.NowMs), new("$run", step.RunId),
            new("$step", c.EntityId), new("$attempt", report.Attempt), new("$generation", report.Generation), new("$session", c.Actor.Id),
            new("$epoch", c.Epoch), new("$hash", body.ReportHash), new("$kind", step.Kind), new("$body", prepared.BodyJson));
        History(s, c, step.RunId, report.Attempt, report.Generation, "receipt_submitted", body.ReceiptId);
        var intent = new ReceiptVerificationIntent(body.ReceiptId, c.EntityId, report.Attempt, report.Generation, body.ReportHash);
        return EngineJournal.Commit(s, c, step.Version + 1, step.Epoch,
            [new("step.changed", "step", c.EntityId, step.Version + 1, false, new(c.Actor.Id, step.RunId, c.EntityId, null, "waiting", step.Epoch)),
             new("workflow.receipt_changed", "receipt", body.ReceiptId, 1, false, new(c.Actor.Id, step.RunId, c.EntityId, null, "pending", step.Epoch))],
            [new("", "workflow_receipt", null, step.RunId, JsonSerializer.SerializeToElement(intent, ReceiptPreparationJson.Default.ReceiptVerificationIntent), c.Actor.Id)]);
    }

    private static bool Oid(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private sealed record Assignment(long Version, string RunId, string State, int Attempt, int Generation, string? Kind,
        string Machine, string RunState, string? SessionId, string? AuthoritySession, long? AuthorityEpoch, long Epoch,
        string SessionMachine, string Holder, string SessionState, string? WorkspaceId, long WorkspaceVersion, string? WorkspaceState, string? StartFingerprint, string? DispatchState);
    private static Assignment? Read(IReadScope s, string stepId) => s.QueryFirst("""
        SELECT st.version,st.run_id,st.state,st.attempt,st.generation,st.result,r.machine,r.state,st.session_id,
          st.authority_session_id,st.authority_epoch,se.epoch,se.machine,se.holder,se.state,st.workspace_id,
          COALESCE(w.version,0),w.state,a.fingerprint,st.dispatch_state FROM engine_step st JOIN engine_run r ON r.id=st.run_id
          JOIN engine_session se ON se.id=st.session_id
          LEFT JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=st.attempt AND a.generation=st.generation
          LEFT JOIN engine_workflow_workspace w ON w.id=st.workspace_id WHERE st.id=$id
        """, r => new Assignment(r.Long(0), r.Text(1), r.Text(2), (int)r.Long(3), (int)r.Long(4), r.IsNull(5) ? null : r.Text(5),
            r.Text(6), r.Text(7), r.IsNull(8) ? null : r.Text(8), r.IsNull(9) ? null : r.Text(9), r.IsNull(10) ? null : r.Long(10),
            r.Long(11), r.Text(12), r.Text(13), r.Text(14), r.IsNull(15) ? null : r.Text(15), r.Long(16), r.IsNull(17) ? null : r.Text(17),
            r.IsNull(18) ? null : r.Text(18), r.IsNull(19) ? null : r.Text(19)), new SqlArg("$id", stepId));
    private static void History(IWriteScope s, CommandContext c, string runId, int attempt, int generation, string kind, string receiptId) => s.Execute("""
        INSERT INTO engine_workflow_history(id,version,machine,created_ms,updated_ms,run_id,history_seq,step_id,attempt,generation,kind,at_ms,safe_body_json)
        SELECT $id,1,$machine,$now,$now,$run,COALESCE(MAX(history_seq),0)+1,$step,$attempt,$generation,$kind,$now,json_object('receiptId',$receipt)
        FROM engine_workflow_history WHERE run_id=$run
        """, new("$id", c.CommandId.ToString("N") + "/" + kind), new("$machine", c.Machine), new("$now", c.NowMs), new("$run", runId),
        new("$step", c.EntityId), new("$attempt", attempt), new("$generation", generation), new("$kind", kind), new("$receipt", receiptId));
}
