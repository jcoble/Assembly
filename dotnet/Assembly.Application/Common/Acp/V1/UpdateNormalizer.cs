using System.Text.Json;
using System.Text.Json.Nodes;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>
/// Version 1 updates in the version 2 shape, so the reader and everything above it stay version 2. A version 1
/// <c>tool_call</c> creates what version 2's <c>tool_call_update</c> upserts, and a version 1 diff names its file as
/// <c>path</c> where version 2 lists <c>changes</c>. Version 1 has no tool <c>name</c>, so a tool is named by its title,
/// else its kind unless that is <c>other</c>; the name is what the engine records the call under, and with the name equal to
/// the title the row reads as before. Every other update is already the same.
/// </summary>
internal static class UpdateNormalizer
{
    public static SessionNotification Normalize(SessionNotification update)
    {
        if (update.Kind is not ("tool_call" or "tool_call_update") || update.Update.ValueKind != JsonValueKind.Object)
            return update;
        var tool = JsonNode.Parse(update.Update.GetRawText())!.AsObject();
        var created = update.Kind == "tool_call";
        tool["sessionUpdate"] = "tool_call_update";
        if (tool["name"] is null && Name(tool, created) is { } name) tool["name"] = name;
        Diffs(tool);
        return update with { Update = Element(tool) };
    }

    /// <summary>A title names any update; a kind other than <c>other</c> names only the call's creation.</summary>
    private static string? Name(JsonObject tool, bool created) =>
        Text(tool, "title") ?? (created && Text(tool, "kind") is { } kind && kind != "other" ? kind : null);

    private static string? Text(JsonObject node, string name) =>
        node[name]?.GetValueKind() == JsonValueKind.String && node[name]!.GetValue<string>() is { } text
        && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>Turns version 1 file snapshots into the changes and Git patch read by the version 2 path.</summary>
    public static void Diffs(JsonObject tool)
    {
        if (tool["content"] is not JsonArray content) return;
        foreach (var item in content.OfType<JsonObject>())
        {
            if (Text(item, "type") != "diff" || item["changes"] is not null || Text(item, "path") is not { } path)
                continue;
            item["changes"] = new JsonArray(new JsonObject { ["path"] = path });
            var oldText = FileText(item, "oldText");
            var newText = FileText(item, "newText");
            if (item["patch"] is null && DiffPatch.FromSnapshots(path, oldText, newText) is { } patch)
                item["patch"] = new JsonObject { ["format"] = "git_patch", ["text"] = patch };
        }
    }

    private static string? FileText(JsonObject item, string key) =>
        item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
