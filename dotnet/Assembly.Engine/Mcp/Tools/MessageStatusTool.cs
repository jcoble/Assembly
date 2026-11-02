using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Mcp.Tools;

public sealed record MessageStatusArguments(string? MessageId = null);
public sealed record MessageStatusAnswer(string MessageId, string State, string? Reason);

public sealed class MessageStatusTool(McpGate gate)
    : EngineTool("message_status", "Read the current state and reason for a message you sent.", """
        {"type":"object","additionalProperties":false,"required":["messageId"],"properties":{
            "messageId":{"type":"string","minLength":1}}}
        """)
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Bad("arguments", "must be an object");
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name != "messageId") throw Bad(property.Name, "is not a supported argument");
            if (property.Value.ValueKind != JsonValueKind.String) throw Bad(property.Name, "must be a string");
        }
        var args = arguments.Deserialize(McpJson.Default.MessageStatusArguments)!;
        if (string.IsNullOrWhiteSpace(args.MessageId)) throw Bad("messageId", "must name a message");
        var answer = gate.Read(caller, (row, scope) => scope.QueryFirst("""
            SELECT m.state,m.reason FROM engine_message m
            JOIN engine_session sender ON sender.id=m.from_session_id
            WHERE m.id=$id AND m.from_session_id=$caller AND m.machine=sender.machine
            """, r => new MessageStatusAnswer(args.MessageId, r.Text(0), r.IsNull(1) ? null : r.Text(1)),
            new SqlArg("$id", args.MessageId), new("$caller", row.SessionId)) ?? throw new McpRefused(new("not_yours")), cancellationToken);
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(answer, McpJson.Default.MessageStatusAnswer));
    }

    private static McpRefused Bad(string field, string problem) =>
        new(new("bad_args", new Dictionary<string, string> { ["field"] = field, ["problem"] = problem }));
}
