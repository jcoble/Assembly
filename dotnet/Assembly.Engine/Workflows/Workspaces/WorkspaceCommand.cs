namespace Assembly.Engine.Workflows.Workspaces;

public sealed record PrepareWorkspace(string? StepId, int Generation, string AllocationId, string BaseHead, string Mode) : WorkflowCommand;
public sealed record WorkspacePrepared(string AllocationId, string Path, string Branch, string BaseHead, string Head, string? Error) : WorkflowCommand;

/// <summary>Exact persisted allocation, committed before the host touches Git or the proof directory.</summary>
public sealed record WorkspaceIntent(string RunId, string? StepId, int Generation, string AllocationId,
    string Repository, string Path, string Branch, string BaseHead, bool Owned, string ProofDir, string DefinitionHash);
