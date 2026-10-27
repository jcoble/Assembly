using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Assembly.Application.Features.Chat.Helpers;

using Assembly.Infrastructure.Http;
namespace Assembly.Infrastructure.Mcp;

/// <summary>
/// <c>POST /mcp</c>: the Assembly MCP server, over MCP's streamable HTTP with plain JSON answers and tools only. Each
/// request carries a session's own token (<see cref="SessionTokens.ServersFor"/>), not the API's, and the tools act
/// for that session. A notification gets 202 and no body.
/// </summary>
internal sealed class McpRoutes(HelperSessions helpers)
{
    private const string ProtocolVersion = "2025-06-18";

    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/mcp", HandleAsync);

    private async Task HandleAsync(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || helpers.Tokens.SessionOf(header["Bearer ".Length..]) is not { } sessionId)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized");
            return;
        }

        if (await JsonReply.ReadObjectAsync(context) is not { } request || JsonReply.String(request, "method") is not { } method)
        {
            await JsonReply.ErrorAsync(context, StatusCodes.Status400BadRequest, "expected a JSON-RPC request");
            return;
        }

        if (!request.TryGetProperty("id", out var id))
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        var parameters = request.TryGetProperty("params", out var given) ? given : default;
        switch (method)
        {
            case "initialize":
                await ResultAsync(context, id, json =>
                {
                    json.WriteString("protocolVersion",
                        parameters.ValueKind == JsonValueKind.Object && JsonReply.String(parameters, "protocolVersion") is { } asked ? asked : ProtocolVersion);
                    json.WriteStartObject("capabilities");
                    json.WriteStartObject("tools");
                    json.WriteEndObject();
                    json.WriteEndObject();
                    json.WriteStartObject("serverInfo");
                    json.WriteString("name", "assembly");
                    json.WriteString("version", "0.1");
                    json.WriteEndObject();
                });
                return;
            case "ping":
                await ResultAsync(context, id, _ => { });
                return;
            case "tools/list":
                await ResultAsync(context, id, helpers.Tools.WriteTools);
                return;
            case "tools/call" when parameters.ValueKind == JsonValueKind.Object && JsonReply.String(parameters, "name") is { } name:
                var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
                var result = await helpers.Tools.CallAsync(sessionId, name, arguments, context.RequestAborted);
                await ResultAsync(context, id, json =>
                {
                    json.WriteStartArray("content");
                    json.WriteStartObject();
                    json.WriteString("type", "text");
                    json.WriteString("text", result.Text);
                    json.WriteEndObject();
                    json.WriteEndArray();
                    json.WriteBoolean("isError", result.IsError);
                });
                return;
            default:
                await JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
                {
                    json.WriteStartObject();
                    json.WriteString("jsonrpc", "2.0");
                    json.WritePropertyName("id");
                    id.WriteTo(json);
                    json.WriteStartObject("error");
                    json.WriteNumber("code", -32601);
                    json.WriteString("message", $"no method {method}");
                    json.WriteEndObject();
                    json.WriteEndObject();
                });
                return;
        }
    }

    private static Task ResultAsync(HttpContext context, JsonElement id, Action<Utf8JsonWriter> result) =>
        JsonReply.WriteAsync(context, StatusCodes.Status200OK, json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WritePropertyName("id");
            id.WriteTo(json);
            json.WriteStartObject("result");
            result(json);
            json.WriteEndObject();
            json.WriteEndObject();
        });
}
