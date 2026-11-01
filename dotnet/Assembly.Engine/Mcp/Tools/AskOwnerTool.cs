using System.Text.Json;
using Assembly.Engine.NeedsMe;

namespace Assembly.Engine.Mcp.Tools;

public sealed record AskOwnerArguments(string? Question = null, string? Kind = null,
    IReadOnlyList<string>? Options = null, string? RunId = null);
public sealed record AskOwnerAnswer(string NeedId, string State);

public sealed class AskOwnerTool(McpGate gate)
    : EngineTool("ask_owner", "Ask Jesse a question, choice or confirmation. Only top-level sessions may ask; children must message their starter with send_message. The answer arrives as an Assembly message.", """
        {"type":"object","additionalProperties":false,"required":["question"],"properties":{
            "question":{"type":"string","minLength":1,"maxLength":1200},
            "kind":{"type":"string","enum":["question","choice","confirmation"]},
            "options":{"type":"array","minItems":2,"maxItems":8,"uniqueItems":true,
                "items":{"type":"string","minLength":1,"maxLength":120}},
            "runId":{"type":"string","minLength":1}}}
        """)
{
    protected override ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Bad("arguments", "must be an object");
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("question" or "kind" or "options" or "runId"))
                throw Bad(property.Name, "is not a supported argument");
            if (property.Name == "options")
            {
                if (property.Value.ValueKind != JsonValueKind.Array ||
                    property.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                    throw Bad("options", "must be an array of strings");
            }
            else if (property.Value.ValueKind != JsonValueKind.String)
                throw Bad(property.Name, "must be a string");
        }
        var args = arguments.Deserialize(McpJson.Default.AskOwnerArguments)!;
        if (string.IsNullOrWhiteSpace(args.Question) || args.Question.Length > 1200)
            throw Bad("question", "must contain 1 to 1200 characters");
        var kind = args.Kind ?? "question";
        if (kind is not ("question" or "choice" or "confirmation"))
            throw Bad("kind", "must be question, choice or confirmation");
        if (kind == "choice" && args.Options is null || args.Options is not null &&
            (args.Options.Count is < 2 or > 8 || args.Options.Distinct(StringComparer.Ordinal).Count() != args.Options.Count))
            throw Bad("options", "needs 2 to 8 distinct entries");
        if (args.Options?.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 120) == true)
            throw Bad("options", "entries must contain 1 to 120 characters");
        if (args.RunId is not null && string.IsNullOrWhiteSpace(args.RunId))
            throw Bad("runId", "must name a run you own");
        var answer = gate.Write(caller, write =>
        {
            var id = Guid.NewGuid().ToString();
            write.Apply(id, 0, write.Caller.Epoch, new OpenNeed(write.Caller.SessionId,
                args.RunId, null, kind, args.Question, args.Options ?? [], null, null, null));
            return new AskOwnerAnswer(id, "open");
        });
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(answer, McpJson.Default.AskOwnerAnswer));
    }

    private static McpRefused Bad(string field, string problem) =>
        new(new("bad_args", new Dictionary<string, string> { ["field"] = field, ["problem"] = problem }));
}
