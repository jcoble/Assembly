using System.Text.Json;

using Assembly.Domain.Rows;
namespace Assembly.Application.Features.Chat.Rows;

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

    /// <summary>The path the call named, joined onto the folder the call ran in when it is relative.</summary>
    public static string? Path(JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } fields)
            return null;
        foreach (var key in (string[])["file_path", "path"])
            if (fields.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return InFolder(value.GetString(), input);
        return null;
    }

    /// <summary>
    /// A relative path joined onto the call's own <c>workdir</c>, else its <c>cwd</c>, when that folder is absolute; a leading
    /// <c>./</c> is dropped because a path with a <c>.</c> component can't be opened. Anything else is unchanged.
    /// </summary>
    public static string? InFolder(string? path, JsonElement? input)
    {
        if (path is null || System.IO.Path.IsPathRooted(path) || path.StartsWith('~') || input is not { ValueKind: JsonValueKind.Object } fields)
            return path;
        foreach (var key in (string[])["workdir", "cwd"])
        {
            if (!fields.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } folder)
                continue;
            if (!System.IO.Path.IsPathRooted(folder))
                return path;
            while (path.StartsWith("./", StringComparison.Ordinal))
                path = path[2..];
            return System.IO.Path.Join(folder, path);
        }
        return path;
    }
}
