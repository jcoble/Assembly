using System.Text.Json.Serialization;

namespace Assembly.Engine.Workflows.Runs;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PauseWorkflow), "pause")]
[JsonDerivedType(typeof(ResumeWorkflow), "resume")]
[JsonDerivedType(typeof(RetryWorkflowStep), "retry")]
[JsonDerivedType(typeof(DecideWorkflowStep), "decide")]
public abstract record WorkflowControl : WorkflowCommand;
public sealed record PauseWorkflow() : WorkflowControl;
public sealed record ResumeWorkflow() : WorkflowControl;
public sealed record RetryWorkflowStep(string StepId) : WorkflowControl;
public sealed record DecideWorkflowStep(string StepId, string Decision, string? Reply) : WorkflowControl;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowControl))]
[JsonSerializable(typeof(string))]
internal sealed partial class WorkflowControlJson : JsonSerializerContext;
