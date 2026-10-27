using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>The Assembly MCP server's tools: listed as <c>tools/list</c> describes them, and called by name.</summary>
public sealed class HelperToolList(SessionStore sessionStore, IReadOnlyList<HelperTool> tools)
{
    public void WriteTools(Utf8JsonWriter json)
    {
        json.WriteStartArray("tools");
        foreach (var tool in tools)
        {
            json.WriteStartObject();
            json.WriteString("name", tool.Name);
            json.WriteString("description", tool.Description);
            json.WriteStartObject("inputSchema");
            json.WriteString("type", "object");
            json.WriteStartObject("properties");
            foreach (var (parameter, about, _) in tool.Parameters)
            {
                json.WriteStartObject(parameter);
                json.WriteString("type", "string");
                json.WriteString("description", about);
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteStartArray("required");
            foreach (var (parameter, _, required) in tool.Parameters)
            {
                if (required)
                    json.WriteStringValue(parameter);
            }
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    /// <summary>Runs a tool for the agent of <paramref name="callerId"/>.</summary>
    public Task<ToolResult> CallAsync(string callerId, string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (sessionStore.Get(callerId) is not { } caller)
            return Task.FromResult(ToolResult.Error("Your session no longer exists."));
        return tools.FirstOrDefault(tool => tool.Name == name) is { } found
            ? found.CallAsync(caller, arguments, cancellationToken)
            : Task.FromResult(ToolResult.Error($"There is no tool named {name}."));
    }
}
