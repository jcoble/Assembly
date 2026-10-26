using System.Text.Json;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>One of the Assembly MCP server's tools: its name, what it does, its string parameters, and its call.</summary>
public abstract class HelperTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract (string Name, string Description, bool Required)[] Parameters { get; }

    /// <summary>Runs it for the agent of <paramref name="caller"/>.</summary>
    public abstract Task<ToolResult> CallAsync(ThreadInfo caller, JsonElement arguments, CancellationToken cancellationToken);

    protected static string? Argument(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
