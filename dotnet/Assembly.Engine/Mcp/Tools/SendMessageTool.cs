using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Mcp.Tools;

public sealed record SendMessageArguments(string? SessionId = null, string? Text = null);
public sealed record SendMessageAnswer(string MessageId, string State);

public sealed class SendMessageTool(McpGate gate)
    : EngineTool("send_message", "Send a message to a reachable session. Returns its message id and queued state.", """
        {"type":"object","additionalProperties":false,"required":["sessionId","text"],"properties":{
            "sessionId":{"type":"string","minLength":1},"text":{"type":"string","minLength":1}}}
        """)
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Bad("arguments", "must be an object");
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("sessionId" or "text")) throw Bad(property.Name, "is not a supported argument");
            if (property.Value.ValueKind != JsonValueKind.String) throw Bad(property.Name, "must be a string");
        }
        var args = arguments.Deserialize(McpJson.Default.SendMessageArguments)!;
        if (string.IsNullOrWhiteSpace(args.SessionId)) throw Bad("sessionId", "must name a session");
        if (string.IsNullOrWhiteSpace(args.Text)) throw Bad("text", "must contain nonblank text");
        var answer = gate.Write(caller, write =>
        {
            var target = write.Scope.QueryFirst("""
                SELECT target.machine,target.state,sender.machine
                FROM engine_session target JOIN engine_session sender ON sender.id=$caller
                WHERE target.id=$target
                """, r => new { Machine = r.Text(0), State = r.Text(1), CallerMachine = r.Text(2) },
                new SqlArg("$caller", write.Caller.SessionId), new("$target", args.SessionId));
            if (target is null) throw new McpRefused(new("not_reachable"));
            if (target.State == "foreign" || target.Machine != target.CallerMachine) throw new McpRefused(new("other_machine"));
            var id = Guid.NewGuid().ToString();
            write.ApplyHelperPrompt(args.SessionId, id, args.Text);
            return new SendMessageAnswer(id, "queued");
        });
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(answer, McpJson.Default.SendMessageAnswer));
    }

    private static McpRefused Bad(string field, string problem) =>
        new(new("bad_args", new Dictionary<string, string> { ["field"] = field, ["problem"] = problem }));
}
