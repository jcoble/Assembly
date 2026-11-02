using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Engine.Mcp;

public abstract class EngineTool : McpServerTool
{
    protected EngineTool(string name, string description, string inputSchemaJson)
    {
        using var schema = JsonDocument.Parse(inputSchemaJson);
        ProtocolTool = new() { Name = name, Description = description, InputSchema = schema.RootElement.Clone() };
    }

    public override Tool ProtocolTool { get; }
    public override IReadOnlyList<object> Metadata { get; } = [];

    protected abstract ValueTask<JsonElement> CallAsync(McpCaller caller, JsonElement arguments, CancellationToken cancellationToken);

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default) =>
        McpInvocation.InvokeAsync(ProtocolTool.Name, request, InvokeJsonAsync, cancellationToken);

    private async ValueTask<CallToolResult> InvokeJsonAsync(McpCaller caller, JsonElement arguments, CancellationToken ct)
    {
        var answer = await CallAsync(caller, arguments, ct);
        return McpInvocation.Text(answer.GetRawText(), false);
    }
}
