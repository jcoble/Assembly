using System.Text.Json;

namespace Assembly.Engine.Rows;

/// <summary>What a tool call acted on, for its one line: the first of these input fields that it has.</summary>
internal static class ToolSummary
{
    // Assembly's order (normalization/tools.rs).
    private static readonly string[] Keys = ["command", "cmd", "description", "query", "pattern", "file_path", "path", "id"];

    public static string? Of(JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } fields)
            return null;
        foreach (var key in Keys)
        {
            if (!fields.TryGetProperty(key, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                return text;
            // Codex sends a command as its argv.
            if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(part => part.ValueKind == JsonValueKind.String))
                return string.Join(' ', value.EnumerateArray().Select(part => part.GetString()));
        }

        return null;
    }

    public static string? Path(JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } fields)
            return null;
        foreach (var key in (string[])["file_path", "path"])
            if (fields.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }
}
