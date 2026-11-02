using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assembly.Engine.Mcp;

/// <summary>The shared SDK caller, generated arguments and refusal boundary for Assembly tools.</summary>
public static class McpInvocation
{
    public static async ValueTask<CallToolResult> InvokeAsync(string name, RequestContext<CallToolRequestParams> request,
        Func<McpCaller, JsonElement, CancellationToken, ValueTask<CallToolResult>> call, CancellationToken ct)
    {
        try
        {
            var session = request.User?.FindFirst("assembly:session")?.Value;
            var epochText = request.User?.FindFirst("assembly:epoch")?.Value;
            if (string.IsNullOrWhiteSpace(session) || !long.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
                throw new McpRefused(new("session_gone"));
            var arguments = JsonSerializer.SerializeToElement(
                request.Params?.Arguments is { } supplied ? new Dictionary<string, JsonElement>(supplied) : [],
                McpJson.Default.DictionaryStringJsonElement);
            return await call(new(session, epoch), arguments, ct);
        }
        catch (McpRefused error) { return Text(Refusals.Text(name, error.Refusal), true); }
        catch (JsonException) { return Text(Refusals.Text(name, new("bad_args")), true); }
    }

    public static CallToolResult Text(string text, bool isError) =>
        new() { IsError = isError, Content = [new TextContentBlock { Text = text }] };
}
