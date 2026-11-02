using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Engine.Effects;
using Assembly.Engine.Mcp;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Infrastructure.Engine.Workflows;

public sealed record WorkflowReceiptServices(
    Func<McpCaller, CancellationToken, Task<ReceiptReportCapture?>> Capture,
    IReceiptChecks Checks, IEngineEffects Effects);

public static class WorkflowReceipts
{
    /// <summary>Composes trusted host producers. Missing evidence observations never produce acceptance.</summary>
    public static WorkflowReceiptServices Create(IAppDatabase db, string machine,
        Func<McpCaller, string?> currentExecution,
        Func<ReceiptReportCapture, CancellationToken, ValueTask<bool>> released,
        Func<ReceiptEvidenceRequest, CancellationToken, Task<ReceiptEvidenceResult>>? checkEvidence = null,
        IGit? git = null)
    {
        var report = new ReceiptReportPreparation(db, machine, currentExecution);
        var checks = new ReceiptChecks(db, machine, released, git, checkEvidence);
        return new(report.CaptureAsync, checks, new ReceiptEffects(checks));
    }
}
