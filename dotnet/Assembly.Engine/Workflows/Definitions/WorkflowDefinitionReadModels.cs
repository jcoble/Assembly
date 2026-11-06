using System.Text.Json.Serialization;

namespace Assembly.Engine.Workflows.Definitions;

public sealed record WorkflowDefinitionPage(IReadOnlyList<WorkflowDefinitionDisplay> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Next);
public sealed record WorkflowDefinitionDisplay(string Path, string? Name, string? Hash, IReadOnlyList<string> Errors);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowDefinitionPage))]
[JsonSerializable(typeof(WorkflowDefinitionDisplay))]
internal sealed partial class WorkflowDefinitionReadJson : JsonSerializerContext;
