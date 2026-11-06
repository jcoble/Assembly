using System.Text.Json;
using Assembly.Engine.Sessions;

namespace Assembly.Engine.Mcp.Tools;

public sealed record ListSessionsArguments;
public sealed record ListedSession(string SessionId, string Title, string Provider, string? Model, string State, string Relation,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? TitleComplete = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? TitleUtf8Bytes = null);
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
        var sessions = gate.Read(caller, (row, scope) => SessionDirectory.ReachableDisplay(scope, row.SessionId)
            .Select(value => new ListedSession(value.Entry.SessionId, value.Entry.Title, value.Entry.Provider, value.Entry.Model,
                value.Entry.State, value.Entry.Relation, value.Complete ? null : false, value.Complete ? null : value.Bytes)).ToArray(), cancellationToken);
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new ListSessionsAnswer(sessions), McpJson.Default.ListSessionsAnswer));
    }
}
