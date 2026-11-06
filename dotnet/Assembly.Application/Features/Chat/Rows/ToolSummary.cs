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
    /// A relative path joined onto the folder the call ran in. The call's own <c>workdir</c>, else its <c>cwd</c>, when
    /// absolute, gives an absolute path. With none, the session's folder is the call's (with a relative call folder
    /// inside it): a path that stays inside it is kept relative to it, folded, so it opens as the session's file and
    /// follows a moved worktree; one that leaves it becomes absolute. A join is folded lexically, since a path with a
    /// <c>.</c> or <c>..</c> component can't be opened: <c>/w/app</c> and <c>../lib/a.rs</c> give <c>/w/lib/a.rs</c>, and
    /// one that would climb above <c>/</c> is no path at all. A path that is absolute or under <c>~</c>, a call folder
    /// under <c>~</c>, or no absolute folder to join leaves the path unchanged.
    /// </summary>
    public static string? InFolder(string? path, JsonElement? input, string? sessionFolder = null)
    {
        if (path is null || System.IO.Path.IsPathRooted(path) || path.StartsWith('~'))
            return path;
        var callFolder = CallFolder(input);
        if (callFolder is not null && System.IO.Path.IsPathRooted(callFolder))
            return Folded(callFolder + '/' + path);
        if (callFolder is not null && callFolder.StartsWith('~'))
            return path;
        if (sessionFolder is null || !System.IO.Path.IsPathRooted(sessionFolder) || Folded(sessionFolder) is not { } root)
            return path;
        var joined = Folded(root + '/' + (callFolder is null ? path : callFolder + '/' + path));
        var inside = root == "/" ? "/" : root + '/';
        return joined is not null && joined.Length > inside.Length && joined.StartsWith(inside, StringComparison.Ordinal)
            ? joined[inside.Length..]
            : joined;
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
