using System.Text.Json;
using Assembly.Engine.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Infrastructure.Mcp;

/// <summary>Retains the existing Board handler's exact schema, text and error behavior.</summary>
internal sealed class BoardUpdateTool : McpServerTool
{
    private readonly McpGate _gate;
    private readonly BoardTools _board;
    public override Tool ProtocolTool { get; }
    public override IReadOnlyList<object> Metadata { get; } = [];

    public BoardUpdateTool(McpGate gate, BoardTools board)
    {
        _gate = gate;
        _board = board;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            board.WriteTool(json);
            json.Flush();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        var definition = document.RootElement;
        ProtocolTool = new()
        {
            Name = definition.GetProperty("name").GetString()!,
            Description = definition.GetProperty("description").GetString(),
            InputSchema = definition.GetProperty("inputSchema").Clone()
        };
    }

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default) =>
        McpInvocation.InvokeAsync(ProtocolTool.Name, request, CallBoardAsync, cancellationToken);

    private ValueTask<CallToolResult> CallBoardAsync(McpCaller caller, JsonElement arguments, CancellationToken ct)
    {
        var id = _gate.Read(caller, (row, _) => row.SessionId, ct);
        var reported = _board.Call(id, arguments);
        return ValueTask.FromResult(McpInvocation.Text(reported.Text, reported.IsError));
    }
}
