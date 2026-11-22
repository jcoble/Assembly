using System.Text;

namespace Assembly.Application.Common.Acp.V1;

/// <summary>Builds the Git patch ACP v2 carries from the file snapshots ACP v1 sends.</summary>
internal static class DiffPatch
{
    public static string? FromSnapshots(string path, string? oldText, string? newText)
    {
        var before = Lines(oldText ?? "");
        var after = Lines(newText ?? "");
        var start = 0;
        while (start < before.Count && start < after.Count && before[start] == after[start]) start++;
        var removed = before.Count - start;
        var added = after.Count - start;
        while (removed > 0 && added > 0 && before[start + removed - 1] == after[start + added - 1])
        {
            removed--;
            added--;
        }
        if (removed == 0 && added == 0) return null;

        var from = GitPath("a/" + path.TrimStart('/'));
        var to = GitPath("b/" + path.TrimStart('/'));
        var patch = new StringBuilder()
            .Append($"diff --git {from} {to}\n")
            .Append($"--- {(oldText is null ? "/dev/null" : from)}\n")
            .Append($"+++ {(newText is null ? "/dev/null" : to)}\n")
            .Append($"@@ -{(removed == 0 ? start : start + 1)},{removed} +{(added == 0 ? start : start + 1)},{added} @@\n");
        Write(patch, '-', before, start, removed);
        Write(patch, '+', after, start, added);
        return patch.ToString();
    }

    private static List<string> Lines(string text)
    {
        List<string> lines = [];
        for (var start = 0; start < text.Length;)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            lines.Add(text[start..end]);
            start = end;
        }
        return lines;
    }

    private static void Write(StringBuilder patch, char prefix, List<string> lines, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            patch.Append(prefix).Append(lines[i]);
            if (!lines[i].EndsWith('\n')) patch.Append("\n\\ No newline at end of file\n");
        }
    }

    private static string GitPath(string path) => path.IndexOfAny(['"', '\\', '\n', '\r', '\t']) < 0 ? path
        : "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
}
