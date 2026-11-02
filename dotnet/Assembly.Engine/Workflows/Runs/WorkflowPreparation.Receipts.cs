using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Engine.Workflows.Runs;

internal static partial class WorkflowPreparation
{
    internal static string ReceiptHash(JsonElement receipt, CancellationToken ct) => Digest(receipt, ct).Sha;

    internal static PreparedWorkflowCommand Receipt(CommandContext context, SubmitStepReceipt report,
        ReceiptReportCapture capture, CancellationToken ct, bool replay = false)
    {
        var reportValue = report.Receipt ?? JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String);
        var reportHash = ReceiptHash(reportValue, ct);
        var body = new ReceiptBody(context with { NowMs = 0 }, report, capture, context.CommandId.ToString("N"), reportHash);
        var (json, sha, bytes) = Digest(JsonSerializer.SerializeToElement(body, ReceiptPreparationJson.Default.ReceiptBody), ct);
        return new(JsonSerializer.Deserialize(json, ReceiptPreparationJson.Default.ReceiptBody)!, json, sha, bytes, replay);
    }
}
