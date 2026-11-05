using System.Text.Json;

using Assembly.Domain.Rows;
namespace Assembly.Application.Features.Chat.Rows;

/// <summary>What a tool call acted on, for its one line: the first of these input fields that it has.</summary>
internal static class ToolSummary
{
    // Assembly's order (normalization/tools.rs).
    private static readonly string[] Keys = ["command", "cmd", "description", "query", "pattern", "file_path", "path", "id"];

    public static string? Of(JsonElement? input) =>
        FirstOf(input, Keys, parts => string.Join(' ', parts));

    /// <summary>A shell call's command, an argv's parts quoted so a shell reads them back as they were; null when the input has none.</summary>
    public static string? Command(JsonElement? input) =>
        FirstOf(input, ["command", "cmd"], parts => string.Join(' ', parts.Select(Quoted)));

    private static string? FirstOf(JsonElement? input, string[] keys, Func<IEnumerable<string>, string> joinArgv)
    {
        if (input is not { ValueKind: JsonValueKind.Object } fields)
            return null;
        foreach (var key in keys)
        {
            if (!fields.TryGetProperty(key, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                return text;
            // Codex sends a command as its argv.
            if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(part => part.ValueKind == JsonValueKind.String))
                return joinArgv(value.EnumerateArray().Select(part => part.GetString()!));
        }

        return null;
    }

    private static string Quoted(string part) =>
        part.Length > 0 && part.All(c => char.IsLetterOrDigit(c) || "-_./~=:,+@%".Contains(c)) ? part : $"'{part.Replace("'", "'\\''")}'";

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
