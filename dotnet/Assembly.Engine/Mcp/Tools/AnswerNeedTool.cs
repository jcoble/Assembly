using System.Text.Json;
using Assembly.Engine.NeedsMe;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Engine.Mcp.Tools;

public sealed class AnswerNeedTool : McpServerTool
{
    private readonly McpGate _gate;
    private readonly INeedAnswers _needs;

    public AnswerNeedTool(McpGate gate, INeedAnswers needs)
    {
        (_gate, _needs) = (gate, needs);
        using var schema = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"required":["needId","answer"],"properties":{
                "needId":{"type":"string","minLength":1},"answer":{"type":["string","object"]}}}
            """);
        ProtocolTool = new() { Name = "answer_need", Description = "Answer the child question addressed to this session, using its exact supplied option or form values.", InputSchema = schema.RootElement.Clone() };
    }

    public override Tool ProtocolTool { get; }
    public override IReadOnlyList<object> Metadata { get; } = [];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
        => McpInvocation.InvokeAsync(ProtocolTool.Name, request,
            (caller, arguments, work) => Call(caller, arguments, work,
                McpInvocation.RequireOriginalWaitToken(request)), cancellationToken);

    private async ValueTask<CallToolResult> Call(McpCaller caller, JsonElement arguments, CancellationToken work, CancellationToken wait)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new McpRefused(new("bad_args"));
        foreach (var property in arguments.EnumerateObject())
            if (property.Name is not ("needId" or "answer")) throw new McpRefused(new("bad_args"));
        if (!arguments.TryGetProperty("needId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
            !arguments.TryGetProperty("answer", out var answer) || answer.ValueKind is not (JsonValueKind.String or JsonValueKind.Object))
            throw new McpRefused(new("bad_args"));
        _gate.Read(caller, (_, _) => 0, work);
        var needId = id.GetString()!;
        var result = await _needs.ApplyAsync(caller, new(needId, ParentNeedAction.Answer, answer), work, wait).ConfigureAwait(false);
        if (!result.Applied) throw new McpRefused(new(result.Error == "forbidden" ? "not_answerer" : result.Error!));
        var response = JsonSerializer.SerializeToElement(new AskOwnerAnswer(needId, "answered"), McpJson.Default.AskOwnerAnswer);
        return McpInvocation.Text(response.GetRawText(), false);
    }
}
