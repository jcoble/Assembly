using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Assembly.Engine.Effects;

namespace Assembly.Engine.Workflows.Execution;

public abstract record CommandStepCommand : WorkflowCommand;
public sealed record StartCommandStep() : CommandStepCommand;
public sealed record CommandStepEnded(int Attempt, int Generation, int CommandIndex, int? ExitCode,
    string OutputReceiptPath, string OutputSha256, bool Stopped, string? Error) : CommandStepCommand;

/// <summary>Frozen command content belongs to one actual step attempt, never a provider session.</summary>
public sealed record CommandExecutionIntent(string RunId, string StepId, int Generation, int Attempt,
    int CommandIndex, long ExecutionEpoch, string Cwd, string Script,
    ImmutableSortedDictionary<string, string> Bindings, string OutputPath, bool Repeat, string Fingerprint, int CommandCount, WorkflowPrBody? PrBody = null)
{
    public string OrderKey => $"workflow-command/{StepId}/{Generation}/{Attempt}";
}
public sealed record CommandProcessIdentity(int Pid, long StartUtcTicks, int GroupId, string Nonce);
public sealed record CommandProcessObserved(string EffectId, int Generation, int Attempt, int CommandIndex,
    long ExecutionEpoch, CommandProcessIdentity Process) : CommandStepCommand;
public sealed record CommandExecutionReceipt(string EffectId, string IntentSha256, CommandProcessIdentity? Process,
    int? ExitCode, string OutputReceiptPath, string OutputSha256, long OutputBytes, long ObservedBytes,
    bool Truncated, bool Stopped, string? Error, string Summary, string? EndFingerprint, int? ExitedPid = null, bool NotStarted = false);

public interface IWorkflowProcess
{
    Task<EffectReceipt> ExecuteAsync(Effect effect, bool reconcile, CancellationToken ct);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
[JsonSerializable(typeof(CommandExecutionIntent))]
[JsonSerializable(typeof(CommandExecutionReceipt))]
[JsonSerializable(typeof(CommandProcessIdentity))]
[JsonSerializable(typeof(CommandProcessObserved))]
[JsonSerializable(typeof(CommandStepEnded))]
public partial class WorkflowExecutionJson : JsonSerializerContext;
