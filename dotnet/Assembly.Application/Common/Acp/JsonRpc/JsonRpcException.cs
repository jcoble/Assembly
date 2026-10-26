using System.Text.Json;

namespace Assembly.Application.Common.Acp.JsonRpc;

/// <summary>A JSON-RPC error, either received from the agent or raised by us to send back.</summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public const int InvalidParams = -32602;
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;

    public int Code { get; } = code;

    internal static JsonRpcException FromError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : InternalError;
        var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        // Claude's adapter puts the useful part in data.details ("Internal error" alone says nothing).
        if (error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.String)
            message = $"{message}: {details.GetString()}";
        return new JsonRpcException(code, message);
    }
}
