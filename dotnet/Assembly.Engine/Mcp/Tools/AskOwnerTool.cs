using System.Text.Json;
using Assembly.Engine.NeedsMe;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Engine.Mcp.Tools;

public sealed record AskOwnerArguments(string? Question = null, string? Kind = null,
    IReadOnlyList<string>? Options = null, string? RunId = null, string? ChildNeedId = null);
public sealed record AskOwnerAnswer(string NeedId, string State);

public sealed class AskOwnerTool : McpServerTool
{
    private readonly McpGate gate;
    private readonly INeedAnswers needs;

    public AskOwnerTool(McpGate gate, INeedAnswers needs)
    {
        (this.gate, this.needs) = (gate, needs);
        using var schema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"properties":{
            "question":{"type":"string","minLength":1,"maxLength":1200},
            "kind":{"type":"string","enum":["question","choice","confirmation"]},
            "options":{"type":"array","minItems":2,"maxItems":8,"uniqueItems":true,
                "items":{"type":"string","minLength":1,"maxLength":120}},
            "runId":{"type":"string","minLength":1},"childNeedId":{"type":"string","minLength":1}}}
        """);
        ProtocolTool = new() { Name = "ask_owner", Description = "Ask Jesse a question, choice or confirmation. Only top-level sessions may ask; use childNeedId to escalate a child question already addressed to you. The answer arrives as an Assembly message.", InputSchema = schema.RootElement.Clone() };
    }

    public override Tool ProtocolTool { get; }
    public override IReadOnlyList<object> Metadata { get; } = [];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        return McpInvocation.InvokeAsync(ProtocolTool.Name, request, async (caller, arguments, work) =>
        {
            var answer = await CallWithWait(caller, arguments, work, McpInvocation.RequireOriginalWaitToken(request)).ConfigureAwait(false);
            return McpInvocation.Text(answer.GetRawText(), false);
        }, cancellationToken);
    }

    private async ValueTask<JsonElement> CallWithWait(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken, CancellationToken wait)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw Bad("arguments", "must be an object");
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("question" or "kind" or "options" or "runId" or "childNeedId"))
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
        if (args.ChildNeedId is not null)
        {
            if (string.IsNullOrWhiteSpace(args.ChildNeedId) || arguments.EnumerateObject().Count() != 1)
                throw Bad("childNeedId", "must be used alone and name an addressed child question");
            gate.Read(caller, (_, _) => 0, cancellationToken);
            var result = await needs.ApplyAsync(caller, new(args.ChildNeedId, ParentNeedAction.Escalate, null), cancellationToken, wait).ConfigureAwait(false);
            if (!result.Applied) throw new McpRefused(new(result.Error == "forbidden" ? "not_answerer" : result.Error!));
            return JsonSerializer.SerializeToElement(new AskOwnerAnswer(args.ChildNeedId, "open"), McpJson.Default.AskOwnerAnswer);
        }
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
        return JsonSerializer.SerializeToElement(answer, McpJson.Default.AskOwnerAnswer);
    }

    private static McpRefused Bad(string field, string problem) =>
        new(new("bad_args", new Dictionary<string, string> { ["field"] = field, ["problem"] = problem }));
}
