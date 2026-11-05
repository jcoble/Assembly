using System.Text;

namespace Assembly.Application.Features.Chat.Rows.Context;

/// <summary>
/// The paths a shell command may have read or written: candidates only, from a fixed list of commands and any
/// redirect target. Pure, with no disk access; <c>rows/files</c> drops candidates that aren't real files. A relative
/// word is resolved against the base folder when one is known (the input's <c>cwd</c>, then any <c>cd</c>), else kept
/// relative. A leading <c>~/</c> stays as written.
/// </summary>
public static class ShellPaths
{
    private static readonly HashSet<string> Readers = ["cat", "head", "tail", "less", "more", "wc", "nl", "bat", "tee", "touch", "cp", "mv", "sed", "awk", "grep", "rg", "ag"];
    private static readonly HashSet<string> Prefixes = ["if", "then", "do", "else", "sudo", "env", "time"];
    private static readonly HashSet<string> ValueFlags = ["-g", "--glob", "-t", "--type", "-e", "-f"];
    private static readonly HashSet<string> Shells = ["sh", "bash", "zsh"];
    private const int MostPaths = 50;

    public static IReadOnlyList<string> Candidates(string command, string? cwd)
    {
        var found = new List<string>();
        Walk(command, cwd, found, depth: 0);
        return found;
    }

    private enum Kind { Word, Separator, Redirect, HereString }

    private sealed record Token(Kind Kind, string Text);

    private static void Walk(string script, string? cwd, List<string> found, int depth)
    {
        var baseFolder = cwd;
        var segment = new List<Token>();
        foreach (var token in Tokenize(script).Append(new Token(Kind.Separator, ";")))
        {
            if (token.Kind != Kind.Separator)
            {
                segment.Add(token);
                continue;
            }
            baseFolder = Command(segment, baseFolder, found, depth);
            segment.Clear();
            if (found.Count >= MostPaths)
                return;
        }
    }

    /// <summary>Adds one simple command's candidates and returns the base folder after it.</summary>
    private static string? Command(List<Token> tokens, string? baseFolder, List<string> found, int depth)
    {
        var words = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind is Kind.Redirect or Kind.HereString)
            {
                if (i + 1 < tokens.Count && tokens[i + 1].Kind == Kind.Word)
                {
                    if (token.Kind == Kind.Redirect && !tokens[i + 1].Text.StartsWith('&'))
                        Add(tokens[i + 1].Text, baseFolder, found);
                    i++;
                }
                continue;
            }
            words.Add(token.Text);
        }

        var start = 0;
        while (start < words.Count && (Prefixes.Contains(words[start]) || IsAssignment(words[start])))
            start++;
        if (start == words.Count)
            return baseFolder;
        var name = words[start];
        name = name[(name.LastIndexOf('/') + 1)..];
        var args = words.Skip(start + 1).ToList();

        if (name == "cd")
        {
            if (args.Count == 0) return "~";
            var target = args.FirstOrDefault(arg => !arg.StartsWith('-'));
            return target is null || Skipped(target) ? null : Resolve(target, baseFolder);
        }

        if (Shells.Contains(name))
        {
            var flag = args.FindIndex(arg => arg.StartsWith('-') && !arg.StartsWith("--") && arg.Contains('c'));
            if (flag >= 0 && flag + 1 < args.Count && depth < 4)
                Walk(args[flag + 1], baseFolder, found, depth + 1);
            return baseFolder;
        }

        if (!Readers.Contains(name) || (name == "rg" && args.Contains("--files")))
            return baseFolder;
        var flagsDone = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!flagsDone && arg == "--") { flagsDone = true; continue; }
            if (!flagsDone && arg.StartsWith('-') && arg.Length > 1)
            {
                if (ValueFlags.Contains(arg)) i++;
                continue;
            }
            Add(arg, baseFolder, found);
        }
        return baseFolder;
    }

    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        return equals > 0 && (char.IsLetter(word[0]) || word[0] == '_') && word[..equals].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static bool Skipped(string word) =>
        word.Length == 0 || word.IndexOfAny(['$', '`', '*', '?']) >= 0;

    private static void Add(string word, string? baseFolder, List<string> found)
    {
        if (word == "-" || Skipped(word) || found.Count >= MostPaths)
            return;
        var path = Resolve(word, baseFolder);
        if (path.Length == 0 || path is "/" or "~" || path.StartsWith("/dev/") || found.Contains(path))
            return;
        found.Add(path);
    }

    /// <summary><paramref name="word"/> made absolute against <paramref name="baseFolder"/> when that's known, with <c>.</c> and <c>..</c> cleaned up.</summary>
    private static string Resolve(string word, string? baseFolder)
    {
        if (word == "~" || word.StartsWith("~/") || word.StartsWith('/'))
            return Clean(word);
        return Clean(baseFolder is null ? word : $"{baseFolder}/{word}");
    }

    private static string Clean(string path)
    {
        var root = path.StartsWith('/') ? "/" : path == "~" || path.StartsWith("~/") ? "~/" : "";
        var parts = new List<string>();
        foreach (var part in path[(root == "~/" ? 1 : 0)..].Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..") { parts.RemoveAt(parts.Count - 1); continue; }
            if (part == ".." && root != "") continue;
            parts.Add(part);
        }
        var joined = string.Join('/', parts);
        return root == "~/" && joined.Length == 0 ? "~" : root + joined;
    }

    /// <summary>
    /// Words with their quotes removed, separators (<c>; &amp;&amp; || | &amp;</c>, newlines and parentheses) and
    /// redirects. A heredoc's body is skipped; <c>$(...)</c> and backticks stay inside their word.
    /// </summary>
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var word = new StringBuilder();
        var inWord = false;
        var heredocs = new List<(string Delimiter, bool Tabs)>();
        var i = 0;

        void EndWord()
        {
            if (inWord) tokens.Add(new Token(Kind.Word, word.ToString()));
            word.Clear();
            inWord = false;
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\'')
            {
                var close = text.IndexOf('\'', i + 1);
                close = close < 0 ? text.Length : close;
                word.Append(text, i + 1, close - i - 1);
                inWord = true;
                i = close + 1;
            }
            else if (c == '"')
            {
                inWord = true;
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
                        i++;
                    word.Append(text[i++]);
                }
                i++;
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                if (text[i + 1] != '\n') { word.Append(text[i + 1]); inWord = true; }
                i += 2;
            }
            else if (c == '$' && i + 1 < text.Length && text[i + 1] == '(' || c == '`')
            {
                i = Substitution(text, i, word);
                inWord = true;
            }
            else if (c == '\n')
            {
                EndWord();
                tokens.Add(new Token(Kind.Separator, "\n"));
                i++;
                if (heredocs.Count > 0)
                {
                    i = SkipBodies(text, i, heredocs);
                    heredocs.Clear();
                }
            }
            else if (c is ' ' or '\t' or '\r')
            {
                EndWord();
                i++;
            }
            else if (c is ';' or '|' or '(' or ')')
            {
                EndWord();
                var two = c == '|' && i + 1 < text.Length && text[i + 1] == '|';
                tokens.Add(new Token(Kind.Separator, two ? "||" : c.ToString()));
                i += two ? 2 : 1;
            }
            else if (c == '&' && !(tokens.Count > 0 && tokens[^1].Kind == Kind.Redirect && !inWord))
            {
                EndWord();
                if (i + 1 < text.Length && text[i + 1] == '>')
                {
                    var append = i + 2 < text.Length && text[i + 2] == '>';
                    tokens.Add(new Token(Kind.Redirect, append ? "&>>" : "&>"));
                    i += append ? 3 : 2;
                }
                else
                {
                    var two = i + 1 < text.Length && text[i + 1] == '&';
                    tokens.Add(new Token(Kind.Separator, two ? "&&" : "&"));
                    i += two ? 2 : 1;
                }
            }
            else if (c is '>' or '<')
            {
                // A number right before it is the descriptor being redirected, not a word: "2>&1".
                if (inWord && word.ToString().All(char.IsDigit)) { word.Clear(); inWord = false; }
                EndWord();
                if (c == '<' && text.AsSpan(i).StartsWith("<<<"))
                {
                    tokens.Add(new Token(Kind.HereString, "<<<"));
                    i += 3;
                }
                else if (c == '<' && text.AsSpan(i).StartsWith("<<"))
                {
                    i += 2;
                    var tabs = i < text.Length && text[i] == '-';
                    if (tabs) i++;
                    while (i < text.Length && text[i] is ' ' or '\t') i++;
                    var delimiter = new StringBuilder();
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (';' or '|' or '&' or '<' or '>' or '(' or ')'))
                    {
                        if (text[i] is not ('\'' or '"' or '\\')) delimiter.Append(text[i]);
                        i++;
                    }
                    if (delimiter.Length > 0) heredocs.Add((delimiter.ToString(), tabs));
                }
                else
                {
                    var append = i + 1 < text.Length && text[i + 1] == '>' && c == '>';
                    tokens.Add(new Token(Kind.Redirect, append ? ">>" : c.ToString()));
                    i += append ? 2 : 1;
                }
            }
            else
            {
                word.Append(c);
                inWord = true;
                i++;
            }
        }
        EndWord();
        return tokens;
    }

    /// <summary>Appends a <c>$(...)</c> or backtick substitution starting at <paramref name="start"/> whole, and returns the index after it.</summary>
    private static int Substitution(string text, int start, StringBuilder word)
    {
        if (text[start] == '`')
        {
            var close = text.IndexOf('`', start + 1);
            var end = close < 0 ? text.Length : close + 1;
            word.Append(text, start, end - start);
            return end;
        }
        var depth = 0;
        var i = start + 1;
        for (; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) { i++; break; }
        }
        word.Append(text, start, i - start);
        return i;
    }

    /// <summary>The index after the heredoc bodies that start at <paramref name="start"/>, each ending at its delimiter line.</summary>
    private static int SkipBodies(string text, int start, List<(string Delimiter, bool Tabs)> heredocs)
    {
        var i = start;
        foreach (var (delimiter, tabs) in heredocs)
        {
            while (i < text.Length)
            {
                var end = text.IndexOf('\n', i);
                var line = text[i..(end < 0 ? text.Length : end)].TrimEnd('\r');
                i = end < 0 ? text.Length : end + 1;
                if ((tabs ? line.TrimStart('\t') : line) == delimiter)
                    break;
            }
        }
        return i;
    }
}
