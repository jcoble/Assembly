using System.Text.Json;
using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Engine.Workflows.Runs;

internal static partial class WorkflowPreparation
{
    internal static EffectReceipt ReceiptConfirmation(Effect effect, ReceiptChecked check, string? endFingerprint,
        IReadOnlyList<ReceiptMatchedFile> files, IReadOnlyList<ReceiptEvidenceObservation> evidence,
        CancellationToken ct, ReceiptReportCapture? releasedExecution = null, IReadOnlyList<ReceiptObservedCheck>? observedChecks = null,
        ReceiptBuildVerification? buildVerification = null)
    {
        ct.ThrowIfCancellationRequested();
        var intent = effect.Payload.Deserialize(ReceiptPreparationJson.Default.ReceiptVerificationIntent)
            ?? throw new ArgumentException("Missing receipt intent.", nameof(effect));
        if (effect.Kind != "workflow_receipt" || check.ReceiptId != intent.ReceiptId ||
            check.Attempt != intent.Attempt || check.Generation != intent.Generation)
            throw new ArgumentException("Receipt confirmation is not bound to this effect.", nameof(check));
        var value = new ReceiptConfirmationBody(check.ReceiptId, effect.Id, effect.Attempt, effect.RunId, intent, check, endFingerprint, releasedExecution, files, evidence, observedChecks ?? [], buildVerification);
        var (json, sha, bytes) = Digest(JsonSerializer.SerializeToElement(value, ReceiptPreparationJson.Default.ReceiptConfirmationBody), ct);
        var frozen = JsonSerializer.Deserialize(json, ReceiptPreparationJson.Default.ReceiptConfirmationBody)!;
        var descriptor = JsonSerializer.Serialize(new ReceiptConfirmationDescriptor(frozen.Check, sha, bytes),
            ReceiptPreparationJson.Default.ReceiptConfirmationDescriptor);
        var canonical = JsonSerializer.Serialize(new WorkflowPreparedEnvelope("receipt_checked", 1, sha, bytes),
            EngineJson.Default.WorkflowPreparedEnvelope);
        using var document = JsonDocument.Parse(descriptor);
        return new(EffectDisposition.Confirmed, document.RootElement.Clone(), null)
        {
            WorkflowReceiptConfirmation = new(frozen, json, sha, bytes, canonical, descriptor)
        };
    }
}
