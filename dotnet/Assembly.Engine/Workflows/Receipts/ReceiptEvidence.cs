using System.Text.Json;

namespace Assembly.Engine.Workflows.Receipts;

/// <summary>Trusted host evidence inputs; tested HEAD is independently checked against each observation.</summary>
public sealed record ReceiptEvidenceRequest(string ReceiptId, string RunId, string StepId, int Attempt,
    int Generation, string Kind, string TestedHead, string Summary, IReadOnlyList<string> ProofUrls, JsonElement Receipt, string Workspace, string ProofDir);

/// <summary>Tests are real repository test names resolved at TestedHead; this does not assert execution passed.
/// Result owner_review records an exact low-impact reason from the typed build summary for owner review.</summary>
public sealed record ReceiptBuildVerification(string Result, string TestedHead, IReadOnlyList<string> Tests, string? Reason);

public enum ReceiptEvidenceDisposition { Unknown, Unavailable, Verified, Rejected }

/// <summary>Reachable URLs establish authenticated readability only. Checks require their own observed result and HEAD.</summary>
public sealed record ReceiptEvidenceResult(ReceiptEvidenceDisposition Disposition, string? Error,
    IReadOnlyList<ReceiptEvidenceObservation> Evidence, IReadOnlyList<ReceiptObservedCheck> Checks, ReceiptBuildVerification? BuildVerification = null);
