using System.Text;

namespace Assembly.Engine.Workflows.Policy;

// This recognizes commands for policy only. It does not execute, expand or rewrite shell code.
internal static class ShellCommands
{
    internal static IReadOnlyList<string>? Parse(string line, int depth = 0)
    {
        if (depth > 8) return null;
        var result = new List<string>();
        var words = new List<string>();
        StringBuilder? word = null;
        char quote = '\0';
        var groups = 0;
        void EndWord() { if (word is not null) { words.Add(word.ToString()); word = null; } }
        bool EndCommand()
        {
            EndWord();
            var normalized = Normalize(words, depth);
            if (normalized is null) return false;
            result.AddRange(normalized); words.Clear(); return true;
        }
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0' && c == quote) { quote = '\0'; continue; }
            if (c == '\\' && quote != '\'')
            {
                if (++i == line.Length) return null;
                if (line[i] != '\n') (word ??= new()).Append(line[i]);
                continue;
            }
            if (quote != '\'' && (c == '`' || c == '$' && i + 1 < line.Length && line[i + 1] == '('))
            {
                var script = Substitution(line, ref i, c == '`');
                if (script is null) return null;
                var nested = Parse(script, depth + 1);
                if (nested is null) return null;
                result.AddRange(nested);
                word ??= new();
                continue;
            }
            if (quote != '\0') { (word ??= new()).Append(c); continue; }
            if (c is '\'' or '"') { quote = c; word ??= new(); continue; }
            if (c == '&' && (word?.ToString().EndsWith('>') == true || i + 1 < line.Length && line[i + 1] == '>'))
            { (word ??= new()).Append(c); continue; }
            if (c is ';' or '&' or '|' or '\n' or '(' or ')')
            {
                if (c == '(') groups++;
                if (c == ')' && --groups < 0) return null;
                if (!EndCommand()) return null;
            }
            else if (char.IsWhiteSpace(c)) EndWord();
            else (word ??= new()).Append(c);
        }
        if (quote != '\0' || groups != 0 || !EndCommand()) return null;
        return result;
    }

    private static string? Substitution(string line, ref int index, bool backtick)
    {
        var start = index + (backtick ? 1 : 2);
        var nesting = 1;
        char quote = '\0';
        for (var i = start; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && quote != '\'') { i++; continue; }
            if (backtick && c == '`') { index = i; return line[start..i]; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (!backtick && c == '(') nesting++;
            if (!backtick && c == ')' && --nesting == 0) { index = i; return line[start..i]; }
        }
        return null;
    }

    private static IReadOnlyList<string>? Normalize(List<string> words, int depth)
    {
        var offset = 0;
        while (offset < words.Count)
        {
            var value = words[offset];
            var equal = value.IndexOf('=');
            if (equal > 0 && value[..equal].All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ||
                value is "do" or "then" or "else" or "elif" or "if" or "while" or "until" or "{" or "}" or "!" or "done" or "fi")
            { offset++; continue; }
            if (value[(value.LastIndexOf('/') + 1)..] == "env")
            {
                offset++;
                while (offset < words.Count && words[offset].StartsWith('-')) offset++;
                continue;
            }
            break;
        }
        if (offset == words.Count) return [];
        words = words.GetRange(offset, words.Count - offset);
        words[0] = words[0][(words[0].LastIndexOf('/') + 1)..];
        if (words[0] is "sh" or "bash" or "zsh" or "dash" && words.Count > 1 &&
            words[1].StartsWith('-') && !words[1].StartsWith("--", StringComparison.Ordinal) && words[1].Contains('c'))
            return depth >= 8 || words.Count < 3 ? null : Parse(string.Join(' ', words.Skip(2)), depth + 1);
        string[] options = words[0] switch { "git" => ["-C", "-c"], "gh" => ["-R", "--repo"], _ => [] };
        while (words.Count > 2 && options.Contains(words[1])) words.RemoveRange(1, 2);
        return [string.Join(' ', words)];
    }

    internal static bool Glob(string pattern, string text)
    {
        var p = 0; var t = 0; var starP = -1; var starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { starP = p++; starT = t; }
            else if (p < pattern.Length && pattern[p] == text[t]) { p++; t++; }
            else if (starP >= 0) { p = starP + 1; t = ++starT; }
            else return false;
        }
        return pattern.AsSpan(p).IndexOfAnyExcept('*') < 0;
    }
}
