using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// A version 1 <c>session/request_permission</c> in the version 2 shape. Version 1 puts the tool call at the top level
/// (<c>toolCall</c>) with no <c>title</c>; version 2 carries it as <c>subject.toolCall</c> beside a <c>title</c>.
/// </summary>
internal static class PermissionNormalizer
{
    public static JsonElement Normalize(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || parameters.TryGetProperty("subject", out _)
            || !parameters.TryGetProperty("toolCall", out var call) || call.ValueKind != JsonValueKind.Object)
            return parameters;
        var request = JsonNode.Parse(parameters.GetRawText())!.AsObject();
        var tool = request["toolCall"]!.AsObject();
        request.Remove("toolCall");
        UpdateNormalizer.Diffs(tool);
        request["title"] ??= tool["title"]?.DeepClone() ?? "";
        request["subject"] = new JsonObject { ["type"] = "tool_call", ["toolCall"] = tool };
        return UpdateNormalizer.Element(request);
    }
}
