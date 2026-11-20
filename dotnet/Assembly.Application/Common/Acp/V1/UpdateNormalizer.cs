using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// Version 1 updates in the version 2 shape, so the reader and everything above it stay version 2. A version 1
/// <c>tool_call</c> creates what version 2's <c>tool_call_update</c> upserts, and a version 1 diff names its file as
/// <c>path</c> where version 2 lists <c>changes</c>. Every other update is already the same.
/// </summary>
internal static class UpdateNormalizer
{
    public static SessionNotification Normalize(SessionNotification update)
    {
        if (update.Kind is not ("tool_call" or "tool_call_update") || update.Update.ValueKind != JsonValueKind.Object)
            return update;
        var tool = JsonNode.Parse(update.Update.GetRawText())!.AsObject();
        tool["sessionUpdate"] = "tool_call_update";
        Diffs(tool);
        return update with { Update = Element(tool) };
    }

    /// <summary>Gives each version 1 diff item (<c>{type: diff, path}</c>) the version 2 <c>changes</c> list.</summary>
    public static void Diffs(JsonObject tool)
    {
        if (tool["content"] is not JsonArray content) return;
        foreach (var item in content.OfType<JsonObject>())
            if (item["type"]?.GetValueKind() == JsonValueKind.String && item["type"]!.GetValue<string>() == "diff"
                && item["changes"] is null && item["path"]?.GetValueKind() == JsonValueKind.String)
                item["changes"] = new JsonArray(new JsonObject { ["path"] = item["path"]!.GetValue<string>() });
    }

    public static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
