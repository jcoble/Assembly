using System.Text.Json;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Engine.Workflows;

public sealed partial class PreparedWorkflowCommand
{
    internal ReceiptBody? Receipt { get; }
    internal bool ReceiptReplay { get; }
    internal PreparedWorkflowCommand(ReceiptBody receipt, string bodyJson, string sha256, long bodyBytes, bool replay = false)
    {
        Receipt = receipt; ReceiptReplay = replay; BodyJson = bodyJson; Sha256 = sha256; BodyBytes = bodyBytes;
        CanonicalJournal = JsonSerializer.Serialize(new WorkflowPreparedEnvelope("step_receipt", 1, sha256, bodyBytes),
            EngineJson.Default.WorkflowPreparedEnvelope);
    }
}
