using Assembly.Engine.Effects;

namespace Assembly.Engine.Workflows.Receipts;

public sealed record ReceiptMatchedFile(string Path, string RootKind, string Sha256, long Bytes);
public sealed record ReceiptObservedCheck(string Name, string Result, string ReceiptUrl, string TestedHead);
public sealed record ReceiptEvidenceObservation(string Url, string Result, long CheckedMs, string? Sha256, long? Bytes, string TestedHead);
internal sealed record ReceiptConfirmationBody(string ReceiptId, string EffectId, int EffectAttempt, string? RunId,
    ReceiptVerificationIntent Intent, ReceiptChecked Check, string? EndFingerprint, ReceiptReportCapture? ReleasedExecution, IReadOnlyList<ReceiptMatchedFile> Files,
    IReadOnlyList<ReceiptEvidenceObservation> Evidence, IReadOnlyList<ReceiptObservedCheck> Checks);
internal sealed record ReceiptConfirmationDescriptor(ReceiptChecked Check, string MetadataSha256, long BodyBytes);

internal sealed class PreparedReceiptConfirmation(ReceiptConfirmationBody body, string json, string sha256,
    long bodyBytes, string canonicalJournal, string descriptorJson)
{
    internal ReceiptConfirmationBody Body { get; } = body;
    internal string BodyJson { get; } = json;
    internal string Sha256 { get; } = sha256;
    internal long BodyBytes { get; } = bodyBytes;
    internal string CanonicalJournal { get; } = canonicalJournal;
    internal string DescriptorJson { get; } = descriptorJson;
}


internal sealed record WorkflowWorkerReleased(string ReceiptId, string StepId, int Attempt, int Generation,
    string SessionId, long Epoch, string ExecutionId, string WorkspaceId, string Head, string Fingerprint, string? ProviderSessionId);
