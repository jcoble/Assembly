using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Engine.Contracts;
using Assembly.Engine.Workflows.Definitions;

namespace Assembly.Engine.Workflows.Runs;

public sealed record WorkflowStartRequest(Guid CommandId, string ProposedRunId, long ExpectedVersion, long Epoch,
    string OwnerSessionId, string RequestKey, string Path, string? Project, string Request, JsonElement? Task,
    JsonElement Inputs, string? Machine = null);
public sealed record WorkflowStartAck(string RunId, string DefinitionHash, CommandResult Result);

internal sealed record WorkflowStartBody(CommandContext Context, WorkflowStartRequest Request,
    string ProjectId, string ProjectRoot, WorkflowSnapshot Snapshot, WorkflowInputs Inputs,
    ImmutableSortedDictionary<string, string> StepIds, string RequestFingerprint, WorkflowRerunSource? Rerun = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowStartBody))]
internal sealed partial class WorkflowPreparationJson : JsonSerializerContext;
