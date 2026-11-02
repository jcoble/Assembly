using System.Text.Json;
using Assembly.Engine.Workflows.Scheduling;

namespace Assembly.Engine.Workflows;

public sealed partial class PreparedWorkflowCommand
{
    internal PreparedWorkflowCommand(DispatchBody dispatch, string bodyJson, string sha256, long bodyBytes)
    {
        BodyJson = bodyJson; Sha256 = sha256; BodyBytes = bodyBytes;
        CanonicalJournal = JsonSerializer.Serialize(new WorkflowPreparedEnvelope("workflow_dispatch", 1, sha256, bodyBytes), EngineJson.Default.WorkflowPreparedEnvelope);
    }
}
