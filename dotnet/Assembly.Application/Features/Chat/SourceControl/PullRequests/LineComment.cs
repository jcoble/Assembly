using System.Globalization;
using System.Text.RegularExpressions;

namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

public sealed record LineComment(string Path, int Line, string Side, string Body)
{
    private static readonly Regex Header = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.NonBacktracking);
    internal bool IsIn(string? patch)
    {
        if (patch is null || Line < 1 || Side is not ("LEFT" or "RIGHT")) return false;
        var left = 0; var right = 0; var leftCount = 0; var rightCount = 0;
        using var lines = new StringReader(patch);
        while (lines.ReadLine() is { } text)
        {
            if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = Header.Match(text);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out left)
                    || !int.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out right)) return false;
                if (!Count(match.Groups[2], out leftCount) || !Count(match.Groups[4], out rightCount)) return false;
                continue;
            }
            if (text.Length == 0 || text[0] == '\\') continue;
            var old = text[0] is ' ' or '-'; var current = text[0] is ' ' or '+';
            if (!old && !current) { leftCount = rightCount = 0; continue; }
            if ((old && leftCount <= 0) || (current && rightCount <= 0)) continue;
            if ((Side == "LEFT" && old && left == Line) || (Side == "RIGHT" && current && right == Line)) return true;
            if (old) { left++; leftCount--; }
            if (current) { right++; rightCount--; }
        }
        return false;
    }
    private static bool Count(Group group, out int count)
    {
        count = 1;
        return !group.Success || int.TryParse(group.Value, CultureInfo.InvariantCulture, out count);
    }
}
