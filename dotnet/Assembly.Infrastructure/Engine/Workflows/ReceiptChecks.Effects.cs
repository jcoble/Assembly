using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Definitions;
using Assembly.Engine.Workflows.Receipts;
using Assembly.Engine.Workflows.Runs;

namespace Assembly.Infrastructure.Engine.Workflows;

internal sealed partial class ReceiptChecks : IReceiptChecks
{
    private readonly IAppDatabase? _receiptDb;
    private readonly string? _receiptMachine;
    private readonly Func<ReceiptReportCapture, CancellationToken, ValueTask<bool>>? _released;

    private readonly Func<ReceiptEvidenceRequest, CancellationToken, Task<ReceiptEvidenceResult>>? _checkEvidence;

    internal ReceiptChecks(IAppDatabase db, string machine,
        Func<ReceiptReportCapture, CancellationToken, ValueTask<bool>> released, IGit? git = null,
        Func<ReceiptEvidenceRequest, CancellationToken, Task<ReceiptEvidenceResult>>? checkEvidence = null) : this(git)
    { _receiptDb = db; _receiptMachine = machine; _released = released; _checkEvidence = checkEvidence; }

    public async Task<EffectReceipt> CheckAsync(Effect effect, bool reconcile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_receiptDb is null || _released is null) return Unavailable("receipt_checks_unavailable");
        ReceiptVerificationIntent? intent;
        try { intent = effect.Payload.Deserialize(ReceiptPreparationJson.Default.ReceiptVerificationIntent); }
        catch (JsonException) { return Unavailable("receipt_unknown"); }
        if (effect.Kind != "workflow_receipt" || effect.SessionId is not null || intent is null) return Unavailable("receipt_unknown");
        ReceiptBody? original;
        ReceiptCheckContext? context;
        string? kind;
        using (var scope = _receiptDb.OpenSnapshot(cancellationToken))
        {
            var source = scope.QueryFirst("""
                SELECT receipt.body_json,receipt.kind,w.path,w.base_head,a.fingerprint,r.proof_dir,r.snapshot_json
                FROM engine_outbox o JOIN engine_workflow_receipt receipt ON receipt.id=$receipt
                JOIN engine_step st ON st.id=receipt.step_id
                JOIN engine_step_attempt a ON a.step_id=st.id AND a.attempt=receipt.attempt AND a.generation=receipt.generation
                JOIN engine_workflow_workspace w ON w.id=a.workspace_id
                JOIN (SELECT run.id,run.proof_dir,snapshot.snapshot_json FROM engine_run run
                  JOIN engine_workflow_snapshot snapshot ON snapshot.run_id=run.id) r ON r.id=receipt.run_id
                WHERE o.id=$effect AND o.kind='workflow_receipt' AND o.state='inflight' AND o.attempt=$claim
                  AND o.machine=$machine AND o.run_id=receipt.run_id AND o.payload_json=$payload
                  AND receipt.run_id=$run AND receipt.step_id=$step AND receipt.hash=$hash AND receipt.state='pending'
                  AND receipt.attempt=$attempt AND receipt.generation=$generation
                """, r => new
            {
                Body = r.Text(0),
                Kind = r.Text(1),
                Path = r.Text(2),
                Base = r.Text(3),
                Start = r.Text(4),
                Proof = r.Text(5),
                Snapshot = r.Text(6)
            },
                new SqlArg("$receipt", intent.ReceiptId), new("$effect", effect.Id), new("$claim", effect.Attempt),
                new("$machine", _receiptMachine), new("$payload", effect.Payload.GetRawText()), new("$run", effect.RunId),
                new("$step", intent.StepId), new("$hash", intent.Hash), new("$attempt", intent.Attempt), new("$generation", intent.Generation));
            if (source is null) return Unavailable("receipt_unknown");
            original = JsonSerializer.Deserialize(source.Body, ReceiptPreparationJson.Default.ReceiptBody);
            var start = JsonSerializer.Deserialize(source.Snapshot, WorkflowPreparationJson.Default.WorkflowStartBody);
            if (original is null || start is null || original.Report.Receipt is null) return Unavailable("receipt_unknown");
            var key = start.StepIds.SingleOrDefault(pair => pair.Value == intent.StepId).Key;
            if (key is null || !start.Snapshot.Definition.Steps.TryGetValue(key, out var spec)) return Unavailable("receipt_unknown");
            var values = EngineCommands.TemplateValues(scope, start, effect.RunId!);
            var expected = spec.ExpectFiles.Select(pattern => WorkflowTemplates.Render(pattern, values)).ToArray();
            IReadOnlyList<string> earlier = [];
            if (spec.Kind == "review" && spec.RedoTarget is { } targetKey && start.StepIds.TryGetValue(targetKey, out var targetStep))
                earlier = scope.Query("""
                    WITH consumed AS (
                        SELECT safe_body_json FROM engine_workflow_history
                        WHERE run_id=$run AND kind='redo_applied' AND generation<$generation
                          AND json_extract(safe_body_json,'$.targetStepId')=$target
                        ORDER BY history_seq DESC LIMIT 1
                    ), cited AS (
                        SELECT receipt.value receipt_id,CAST(receipt.key AS INTEGER) ordinal FROM consumed,
                            json_each(consumed.safe_body_json,'$.findingReceiptIds') receipt
                    )
                    SELECT json_extract(finding.value,'$.text') FROM cited
                    JOIN engine_workflow_receipt prior ON prior.id=cited.receipt_id AND prior.run_id=$run
                        AND prior.kind='review' AND prior.state='accepted',
                        json_each(prior.body_json,'$.report.receipt.findings') finding
                    ORDER BY cited.ordinal,CAST(finding.key AS INTEGER)
                    """, r => r.Text(0), new SqlArg("$run", effect.RunId), new("$target", targetStep), new("$generation", intent.Generation));
            context = new(source.Path, source.Proof, source.Base, original.Capture.Head, source.Start,
                original.Capture.Fingerprint, earlier, expected);
            kind = source.Kind;
        }
        // The host validates the exact actual execution identity and Done/released state. Idle is insufficient.
        if (original.Capture.ExecutionId is null || !await _released(original.Capture, cancellationToken))
            return Unavailable("provider_execution_not_released");
        var checkedLocal = await CheckLocalAsync(context, kind, original.Report.Receipt!.Value, cancellationToken);
        if (checkedLocal.Error is not null)
            return WorkflowPreparation.ReceiptConfirmation(effect,
                new(intent.ReceiptId, intent.Attempt, intent.Generation, false, checkedLocal.Head, checkedLocal.Error),
                checkedLocal.Fingerprint, [], [], cancellationToken, releasedExecution: original.Capture);
        if (_checkEvidence is null) return Unavailable("evidence_unverified");
        var evidence = await _checkEvidence(new(intent.ReceiptId, effect.RunId!, intent.StepId, intent.Attempt,
            intent.Generation, kind, checkedLocal.Head!, original.Report.Summary,
            original.Report.ProofUrls.ToArray(), original.Report.Receipt.Value.Clone()), cancellationToken);
        if (evidence.Disposition == ReceiptEvidenceDisposition.Unknown)
            return new(EffectDisposition.Unknown, JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), evidence.Error ?? "evidence_unverified");
        if (evidence.Disposition == ReceiptEvidenceDisposition.Unavailable)
            return Unavailable(evidence.Error ?? "evidence_unverified");
        if (evidence.Disposition is not (ReceiptEvidenceDisposition.Verified or ReceiptEvidenceDisposition.Rejected))
            return Unavailable("evidence_unverified");
        // Network observations may take time; acceptance still binds the released execution and unchanged checkout.
        if (!await _released(original.Capture, cancellationToken)) return Unavailable("provider_execution_not_released");
        var after = await _checkout.Capture(context.Workspace, cancellationToken);
        var error = after.Head != checkedLocal.Head || after.Fingerprint != checkedLocal.Fingerprint ? "changed_code"
            : evidence.Disposition == ReceiptEvidenceDisposition.Rejected ? evidence.Error ?? "evidence_rejected" : null;
        var confirmation = WorkflowPreparation.ReceiptConfirmation(effect,
            new(intent.ReceiptId, intent.Attempt, intent.Generation, error is null, after.Head, error), after.Fingerprint,
            checkedLocal.Files.Select(file => new ReceiptMatchedFile(file.Path, file.RootKind, file.Sha256, file.Bytes)).ToArray(),
            evidence.Evidence, cancellationToken, original.Capture, evidence.Checks);
        using var final = _receiptDb.OpenSnapshot(cancellationToken);
        return ReceiptTransitions.ValidConfirmation(final, effect, confirmation) ? confirmation : Unavailable("evidence_unverified");
    }

    private static EffectReceipt Unavailable(string error) => new(EffectDisposition.Unavailable,
        JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), error);
}
