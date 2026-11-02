using System.Text.Json.Serialization;
using Assembly.Engine.Workflows.Reviews;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Engine.Workflows.Receipts;

/// <summary>Trusted owning-host observation, prepared only after actual Done and no kept executor.
/// A committed terminal row or idle session alone never establishes release. This is not a wire command.</summary>
public sealed class PreparedWorkflowRelease
{
    internal WorkflowReleaseBody Body { get; }
    internal string Canonical { get; }
    internal PreparedWorkflowRelease(WorkflowReleaseBody body, string canonical) { Body = body; Canonical = canonical; }
}

internal sealed record WorkflowReleaseBody(SessionObservation Observed, string ChatRunId, long TerminalSeq, string TerminalState, long EndedMs, int Generation, string AuthorityMachine, string DefinitionHash);
internal sealed record WorkflowExecutionReleased(string StepId, int Attempt, int Generation, string SessionId, long Epoch,
    string AuthorityMachine, string ChatRunId, long TerminalSeq, string TerminalState, long EndedMs, string? ProviderSessionId);
internal sealed record WorkflowReportReminder(string SessionId, long Epoch, string ChatRunId, long TerminalSeq, string MessageId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<WorkflowExecutionFence>))]
[JsonSerializable(typeof(WorkflowReleaseBody))]
[JsonSerializable(typeof(WorkflowExecutionReleased))]
[JsonSerializable(typeof(WorkflowReportReminder))]
internal partial class WorkflowReleaseJson : JsonSerializerContext;
