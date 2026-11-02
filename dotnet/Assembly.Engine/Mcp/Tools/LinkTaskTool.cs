using System.Text.Json;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Mcp.Tools;

public sealed record LinkTaskArguments(string? TaskUrl = null);
public sealed record LinkTaskAnswer(string SessionId, string TaskUrl);

public sealed class LinkTaskTool(McpGate gate)
    : EngineTool("link_task", "Link an HTTPS task URL to your own session. A workflow is not required; this does not change a run's task link.", """
        {"type":"object","additionalProperties":false,"required":["taskUrl"],"properties":{
            "taskUrl":{"type":"string","format":"uri","minLength":1}}}
        """)
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Bad("arguments", "must be an object");
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name != "taskUrl") throw Bad(property.Name, "is not a supported argument");
            if (property.Value.ValueKind != JsonValueKind.String) throw Bad(property.Name, "must be a string");
        }
        var args = arguments.Deserialize(McpJson.Default.LinkTaskArguments)!;
        if (!Uri.TryCreate(args.TaskUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || string.IsNullOrEmpty(url.Host))
            throw Bad("taskUrl", "must be an https URL");
        var answer = gate.Write(caller, write =>
        {
            write.Apply(write.Caller.SessionId, write.VersionOf("session", write.Caller.SessionId),
                write.Caller.Epoch, new LinkTask(args.TaskUrl!));
            return new LinkTaskAnswer(write.Caller.SessionId, args.TaskUrl!);
        });
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(answer, McpJson.Default.LinkTaskAnswer));
    }

    private static McpRefused Bad(string field, string problem) =>
        new(new("bad_args", new Dictionary<string, string> { ["field"] = field, ["problem"] = problem }));
}
