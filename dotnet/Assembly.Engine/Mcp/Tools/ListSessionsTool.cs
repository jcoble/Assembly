using System.Text.Json;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Mcp.Tools;

public sealed record ListSessionsArguments;
public sealed record ListedSession(string SessionId, string Title, string Provider, string? Model, string State, string Relation);
public sealed record ListSessionsAnswer(IReadOnlyList<ListedSession> Sessions);

public sealed class ListSessionsTool(McpGate gate)
    : EngineTool("list_sessions", "List related sessions on this machine.", """
        {"type":"object","additionalProperties":false,"properties":{}}
        """)
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new McpRefused(new("bad_args"));
        _ = arguments.Deserialize(McpJson.Default.ListSessionsArguments);
        var sessions = gate.Read(caller, (row, scope) => SessionDirectory.Reachable(scope, row.SessionId)
            .Select(s => new ListedSession(s.SessionId, s.Title, s.Provider, s.Model, s.State, s.Relation)).ToArray(), cancellationToken);
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new ListSessionsAnswer(sessions), McpJson.Default.ListSessionsAnswer));
    }
}
