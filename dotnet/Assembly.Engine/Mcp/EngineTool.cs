using System.Globalization;
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

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = request.User?.FindFirst("assembly:session")?.Value;
            var epochText = request.User?.FindFirst("assembly:epoch")?.Value;
            if (string.IsNullOrWhiteSpace(session) || !long.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
                throw new McpRefused(new("session_gone"));
            // Serialize the SDK's materialized arguments through generated metadata, never reflection.
            var arguments = JsonSerializer.SerializeToElement(
                request.Params?.Arguments is { } supplied ? new Dictionary<string, JsonElement>(supplied) : [],
                McpJson.Default.DictionaryStringJsonElement);
            var answer = await CallAsync(new(session, epoch), arguments, cancellationToken);
            return Result(answer.GetRawText(), false);
        }
        catch (McpRefused error) { return Result(Refusals.Text(ProtocolTool.Name, error.Refusal), true); }
        catch (JsonException) { return Result(Refusals.Text(ProtocolTool.Name, new("bad_args")), true); }
    }

    private static CallToolResult Result(string text, bool error) =>
        new() { IsError = error, Content = [new TextContentBlock { Text = text }] };
}
