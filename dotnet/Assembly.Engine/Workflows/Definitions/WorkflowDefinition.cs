using Assembly.Engine.Workflows.Policy;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assembly.Engine.Workflows.Definitions;

public sealed record WorkflowSource(string Yaml, ImmutableSortedDictionary<string, string> Prompts);
public sealed record WorkflowPromptReference(string Path, string Field);
public sealed record WorkflowSnapshot(int SchemaVersion, string Hash, string Yaml,
    ImmutableSortedDictionary<string, string> Prompts, WorkflowDefinition Definition);
public sealed record WorkflowDefinition(string Name, ImmutableSortedDictionary<string, WorkflowInputSpec> Inputs,
    WorkflowLimits Limits, GuardPolicy Policy, ImmutableSortedDictionary<string, WorkflowStepSpec> Steps);
public sealed record WorkflowInputSpec(string? Description, bool Required, JsonElement? Default);
public sealed record WorkflowLimits(long MaxSessions = 3, long StepTimeoutMinutes = 60, long MaxRounds = 5)
{
    public long StepTimeoutMilliseconds => checked(StepTimeoutMinutes * 60_000);
}
public sealed record WorkflowDisplay(string? Label = null, string? Stage = null);
public sealed record WorkflowStepSpec(string Id, string Kind, ImmutableArray<string> Needs,
    string? Provider, string? Model, string? Permission, string? Prompt, string? Result,
    ImmutableArray<string> Commands, bool Repeat, string? RedoTarget, ImmutableArray<string> ExpectFiles,
    string? Condition, JsonElement? Gate, string Workspace, bool Required, long MaxAttempts, WorkflowDisplay? Display, string? OnFail = null);
public sealed record WorkflowInputs(JsonElement Values, string Hash);
public sealed class WorkflowFieldException(string field, string message) : Exception($"{field}: {message}")
{
    public string Field { get; } = field;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowSnapshot))]
[JsonSerializable(typeof(WorkflowDefinition))]
[JsonSerializable(typeof(WorkflowInputs))]
public partial class WorkflowJson : JsonSerializerContext;
