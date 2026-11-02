using Assembly.Engine.Contracts;

namespace Assembly.Engine.Workflows;

public abstract record WorkflowCommand : EngineCommand;
public sealed record WorkflowPreparedEnvelope(string Kind, int FormatVersion, string Sha256, long BodyBytes);

// Only Engine preparation constructs this owned immutable body. It is never a raw command argument.
public sealed class PreparedWorkflowCommand
{
    private readonly Runs.WorkflowStartBody? _start;
    internal Runs.WorkflowStartBody Start => _start ?? throw new InvalidOperationException("Not a workflow start.");
    internal Workspaces.WorkspaceBody? Workspace { get; }
    internal string BodyJson { get; }
    internal string CanonicalJournal { get; }
    public string Sha256 { get; }
    public long BodyBytes { get; }

    internal PreparedWorkflowCommand(Runs.WorkflowStartBody start, string bodyJson, string sha256, long bodyBytes)
    {
        _start = start; BodyJson = bodyJson; Sha256 = sha256; BodyBytes = bodyBytes;
        CanonicalJournal = System.Text.Json.JsonSerializer.Serialize(new WorkflowPreparedEnvelope("workflow_start", 1, sha256, bodyBytes),
            EngineJson.Default.WorkflowPreparedEnvelope);
    }
    internal PreparedWorkflowCommand(Workspaces.WorkspaceBody workspace, string bodyJson, string sha256, long bodyBytes)
    {
        Workspace = workspace; BodyJson = bodyJson; Sha256 = sha256; BodyBytes = bodyBytes;
        CanonicalJournal = System.Text.Json.JsonSerializer.Serialize(new WorkflowPreparedEnvelope("workspace_prepare", 1, sha256, bodyBytes),
            EngineJson.Default.WorkflowPreparedEnvelope);
    }
}
