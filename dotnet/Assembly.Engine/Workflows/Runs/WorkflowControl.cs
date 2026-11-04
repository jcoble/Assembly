namespace Assembly.Engine.Workflows.Runs;

public sealed record PauseWorkflow : WorkflowCommand;
public sealed record ResumeWorkflow : WorkflowCommand;
public sealed record RetryWorkflowStep(string StepId) : WorkflowCommand;
public sealed record DecideWorkflowStep(string StepId, string Decision, string? Reply = null) : WorkflowCommand;
public sealed record KeepWorkflowSucceeded : WorkflowCommand;
public sealed record UndoWorkflowKeep(string Token) : WorkflowCommand;
public sealed record GrantWorkflowScope(string StepId, string Scope) : WorkflowCommand;
public sealed record SetWorkflowMute(long? UntilMs) : WorkflowCommand;
public record RerunWorkflow(string SourceRunId, string RequestKey) : WorkflowCommand;
public sealed record RetryFailedWorkflow(string SourceRunId, string RequestKey) : RerunWorkflow(SourceRunId, RequestKey);
