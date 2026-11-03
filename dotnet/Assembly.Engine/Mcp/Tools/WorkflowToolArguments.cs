using System.Text.Json;

namespace Assembly.Engine.Mcp.Tools;

public sealed record CheckWorkflowArguments(string? Path = null, string? Project = null);
public sealed record StartWorkflowArguments(string? Path = null, string? Request = null, JsonElement? Task = null,
    JsonElement? Inputs = null, string? Project = null, string? RequestKey = null);
public sealed record WorkflowFormatAnswer(int SchemaVersion, string Text);
public sealed record WorkflowToolError(string Field, string Message);
public sealed record CheckWorkflowAnswer(bool Ok, string? DefinitionHash, IReadOnlyList<WorkflowToolError> Errors);
public sealed record StartWorkflowAnswer(string RunId, string State, string DefinitionHash);

internal static class WorkflowToolArguments
{
    internal static void Validate(JsonElement args, bool start)
    {
        Object(args, "arguments", start ? ["path", "request", "task", "inputs", "project", "requestKey"] : ["path", "project"]);
        foreach (var property in args.EnumerateObject())
        {
            if (property.Name == "inputs")
            {
                if (property.Value.ValueKind != JsonValueKind.Object) throw Bad("inputs", "must be an object");
            }
            else if (property.Name == "task")
            {
                Object(property.Value, "task", ["key", "title", "body", "url"]);
                foreach (var field in new[] { "key", "title", "body" })
                    if (!property.Value.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                        throw Bad("task." + field, "must be a string");
                foreach (var field in property.Value.EnumerateObject())
                    if (field.Value.ValueKind != JsonValueKind.String) throw Bad("task." + field.Name, "must be a string");
                if (property.Value.TryGetProperty("url", out var url) && (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) ||
                    uri.Scheme != "https" || uri.Host.Length == 0)) throw Bad("task.url", "must be an https URL");
            }
            else if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                throw Bad(property.Name, "must be a nonempty string");
        }
        foreach (var field in start ? new[] { "path", "request", "requestKey" } : ["path"])
            if (!args.TryGetProperty(field, out _)) throw Bad(field, "is required");
    }

    internal static void Object(JsonElement value, string field, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Bad(field, "must be an object");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw Bad(field + "." + property.Name, "is not a unique supported argument");
    }

    internal static McpRefused Bad(string field, string problem) => new(new("bad_args",
        new Dictionary<string, string> { ["field"] = field, ["problem"] = problem }));
}
