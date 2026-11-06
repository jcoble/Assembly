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
    public static string? Path(JsonElement? input, string? sessionFolder = null)
    {
        if (input is not { ValueKind: JsonValueKind.Object } fields)
            return null;
        foreach (var key in (string[])["file_path", "path"])
            if (fields.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return InFolder(value.GetString(), input, sessionFolder);
        return null;
    }

    /// <summary>
    /// A relative path joined onto the folder the call ran in: its own <c>workdir</c>, else its <c>cwd</c>, when that folder
    /// is absolute; otherwise the session's folder, with a relative call folder inside it. The join is folded lexically,
    /// since a path with a <c>.</c> or <c>..</c> component can't be opened: <c>/w/app</c> and <c>../lib/a.rs</c> give
    /// <c>/w/lib/a.rs</c>. A join that would climb above <c>/</c> is no path at all. With no absolute folder to join, or
    /// a path that is already absolute or under <c>~</c>, the path is unchanged.
    /// </summary>
    public static string? InFolder(string? path, JsonElement? input, string? sessionFolder = null)
    {
        if (path is null || System.IO.Path.IsPathRooted(path) || path.StartsWith('~'))
            return path;
        var callFolder = CallFolder(input);
        if (callFolder is not null && System.IO.Path.IsPathRooted(callFolder))
            return Folded(callFolder + '/' + path);
        if (sessionFolder is null || !System.IO.Path.IsPathRooted(sessionFolder))
            return path;
        return Folded(sessionFolder + '/' + (callFolder is null ? path : callFolder + '/' + path));
    }

    /// <summary>The call's own <c>workdir</c>, else its <c>cwd</c>, when it names one.</summary>
    private static string? CallFolder(JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } fields)
            return null;
        foreach (var key in (string[])["workdir", "cwd"])
            if (fields.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } folder)
                return folder;
        return null;
    }

    /// <summary>An absolute path with its <c>.</c> parts dropped and each <c>..</c> taking off the part before it, or null
    /// when a <c>..</c> has nothing left to take off.</summary>
    private static string? Folded(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part != "..")
                parts.Add(part);
            else if (parts.Count == 0)
                return null;
            else
                parts.RemoveAt(parts.Count - 1);
        }
        return "/" + string.Join('/', parts);
    }
}
