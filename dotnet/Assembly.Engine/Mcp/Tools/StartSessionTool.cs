using System.Text.Json;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Mcp.Tools;

public sealed record StartSessionArguments(string? Prompt = null, string? Title = null,
    string? Provider = null, string? Model = null, string? Folder = null, string? TaskUrl = null);
public sealed record StartSessionAnswer(string SessionId, string MessageId);

public sealed class StartSessionTool(McpGate gate, Func<string, string?> defaultModel, Func<string, bool> folderExists)
    : EngineTool("start_session", "Start a helper session with a task.", """
        {"type":"object","additionalProperties":false,"required":["prompt","title"],"properties":{
            "prompt":{"type":"string","minLength":1,"maxLength":16000},
            "title":{"type":"string","minLength":1,"maxLength":120},
            "provider":{"type":"string","enum":["claude","codex"]},
            "model":{"type":"string","minLength":1},"folder":{"type":"string","minLength":1},
            "taskUrl":{"type":"string","format":"uri"}}}
        """)
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Bad("arguments", "must be an object");
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("prompt" or "title" or "provider" or "model" or "folder" or "taskUrl"))
                throw Bad(property.Name, "is not a supported argument");
            if (property.Value.ValueKind != JsonValueKind.String) throw Bad(property.Name, "must be a string");
        }
        var args = arguments.Deserialize(McpJson.Default.StartSessionArguments)!;
        if (string.IsNullOrWhiteSpace(args.Prompt) || args.Prompt.Length > 16000)
            throw Bad("prompt", "must contain 1 to 16000 characters");
        if (string.IsNullOrWhiteSpace(args.Title) || args.Title.Length > 120 ||
            args.Title.IndexOfAny(['\r', '\n', '\u0085', '\u2028', '\u2029']) >= 0)
            throw Bad("title", "must be one line of 1 to 120 characters");
        if (args.TaskUrl is not null && (!Uri.TryCreate(args.TaskUrl, UriKind.Absolute, out var task) ||
            task.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(task.Host)))
            throw Bad("taskUrl", "must be an https URL");
        var answer = gate.Write(caller, write =>
        {
            var provider = args.Provider ?? write.Caller.Provider;
            string[] providers = ["claude", "codex"];
            var configured = providers.Where(p => !string.IsNullOrWhiteSpace(defaultModel(p))).ToArray();
            if (!configured.Contains(provider, StringComparer.Ordinal))
                throw Bad("provider", $"must be one of the configured providers: {string.Join(", ", configured)}");
            var model = args.Model ?? (provider == write.Caller.Provider ? write.Caller.Model : defaultModel(provider));
            if (string.IsNullOrWhiteSpace(model)) throw Bad("model", "must name a model");
            var folder = args.Folder ?? write.Caller.Folder;
            if (!Path.IsPathFullyQualified(folder) || !folderExists(folder))
                throw Bad("folder", "must be an absolute existing folder on this machine");
            var childId = Guid.NewGuid().ToString();
            var messageId = Guid.NewGuid().ToString();
            write.Apply(childId, 0, 0, new StartSession(provider, model, folder, args.Title, write.Caller.SessionId, null, args.TaskUrl));
            var prompt = $"Session \"{write.Caller.Title}\" ({write.Caller.SessionId}) started you. Do the task below. Your last reply each turn goes back to it as a message; you can also message it with the Assembly MCP server's send_message tool.\n\n{args.Prompt}";
            write.Apply(messageId, 0, 0, new SendMessage(write.Caller.SessionId, childId, prompt, "prompt"));
            return new StartSessionAnswer(childId, messageId);
        });
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(answer, McpJson.Default.StartSessionAnswer));
    }

    private static McpRefused Bad(string field, string problem) =>
        new(new("bad_args", new Dictionary<string, string> { ["field"] = field, ["problem"] = problem }));
}
