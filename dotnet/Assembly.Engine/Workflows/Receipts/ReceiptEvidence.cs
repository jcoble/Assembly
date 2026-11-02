using System.Text.Json;

namespace Assembly.Engine.Workflows.Receipts;

/// <summary>Trusted host evidence inputs; tested HEAD is independently checked against each observation.</summary>
public sealed record ReceiptEvidenceRequest(string ReceiptId, string RunId, string StepId, int Attempt,
    int Generation, string Kind, string TestedHead, string Summary, IReadOnlyList<string> ProofUrls, JsonElement Receipt);

public enum ReceiptEvidenceDisposition { Unknown, Unavailable, Verified, Rejected }

/// <summary>Reachable URLs establish authenticated readability only. Checks require their own observed result and HEAD.</summary>
public sealed record ReceiptEvidenceResult(ReceiptEvidenceDisposition Disposition, string? Error,
    IReadOnlyList<ReceiptEvidenceObservation> Evidence, IReadOnlyList<ReceiptObservedCheck> Checks);
