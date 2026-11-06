using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Workflows.Receipts;

internal sealed record SubmitStepReceipt(int Attempt, int Generation, string State, string Summary,
    IReadOnlyList<string> ProofUrls, JsonElement? Receipt);

// Only a trusted host captures this value outside the writer; none of these fields are tool arguments.
public sealed record ReceiptReportCapture(string StepId, int Attempt, int Generation, string SessionId, long Epoch,
    string WorkspaceId, long WorkspaceVersion, string Head, string Fingerprint, string? ExecutionId = null);
internal sealed record ReceiptBody(CommandContext Context, SubmitStepReceipt Report,
    ReceiptReportCapture Capture, string ReceiptId, string ReportHash);
public sealed record ReceiptVerificationIntent(string ReceiptId, string StepId, int Attempt, int Generation, string Hash);
public sealed record ReceiptChecked(string ReceiptId, int Attempt, int Generation, bool Accepted, string? Head, string? Error) : WorkflowCommand;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkflowWorkerReleased))]
[JsonSerializable(typeof(ReceiptConfirmationBody))]
[JsonSerializable(typeof(ReceiptConfirmationDescriptor))]
[JsonSerializable(typeof(ReceiptBody))]
[JsonSerializable(typeof(SubmitStepReceipt))]
[JsonSerializable(typeof(ReceiptVerificationIntent))]
[JsonSerializable(typeof(ReceiptChecked))]
internal partial class ReceiptPreparationJson : JsonSerializerContext;
