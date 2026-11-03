using System.Globalization;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>Read-only pages in Git's order, optionally following one literal path across renames.</summary>
public sealed class GitHistoryReader(IGit git)
{
    public const int Page = 24;
    private const int BodyBytes = 8 * 1024;
    private const int PageBytes = 1024 * 1024;

    public async Task<GitHistoryPage> ReadAsync(string root, string? cursor, string? path, CancellationToken ct)
    {
        var skip = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out skip) || skip > int.MaxValue - Page))
            throw new CallFailed("Invalid Git history cursor.");
        var exists = false;
        await foreach (var part in git.ReadAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], null, ct))
            exists |= !string.IsNullOrWhiteSpace(part);
        if (!exists) return new([], null);
        List<string> args = ["--literal-pathspecs", "log", "--decorate=short", "--date=iso-strict",
            "--format=%h%x1f%H%x1f%s%x1f%b%x1f%an%x1f%cI%x1f%D%x1f%P%x00",
            "--skip=" + skip.ToString(CultureInfo.InvariantCulture), "-n25"];
        if (path is not null) args.AddRange(["--follow", "--", path]);
        List<GitCommit> commits = [];
        var record = new LogRecord();
        var bytes = 0;
        await foreach (var piece in git.ReadAsync(root, args, null, ct))
        {
            record.Append(piece);
            // IGit's large-record pieces end at exactly 1 MiB UTF-16 characters; the next piece completes that record.
            if (piece.Length == 1024 * 1024) continue;
            var commit = record.Take();
            if (commit is null) continue;
            bytes += Encoding.UTF8.GetByteCount(commit.ShortSha + commit.Sha + commit.Subject + commit.Body + commit.Author +
                commit.CommittedAt + string.Concat(commit.Refs) + string.Concat(commit.Parents));
            if (bytes > PageBytes) throw new CallFailed("This Git history page exceeds the size limit.");
            commits.Add(commit);
            if (commits.Count == Page + 1) break;
        }
        var more = commits.Count > Page;
        if (more) commits.RemoveAt(Page);
        return new(commits, more ? (skip + Page).ToString(CultureInfo.InvariantCulture) : null);
    }

    private sealed class LogRecord
    {
        private readonly StringBuilder[] _fields = Enumerable.Range(0, 8).Select(_ => new StringBuilder()).ToArray();
        private int _field;
        private int _metadata;

        public void Append(string piece)
        {
            foreach (var ch in piece)
            {
                if (ch == '\u001f')
                {
                    if (++_field >= _fields.Length) throw new CallFailed("Git returned invalid history metadata.");
                    continue;
                }
                if (_field == 3)
                {
                    if (_fields[3].Length < BodyBytes + 1) _fields[3].Append(ch);
                }
                else
                {
                    if (++_metadata > PageBytes) throw new CallFailed("This Git history page exceeds the size limit.");
                    _fields[_field].Append(ch);
                }
            }
        }

        public GitCommit? Take()
        {
            var fields = _fields.Select(field => field.ToString()).ToArray();
            var count = _field;
            foreach (var field in _fields) field.Clear();
            _field = _metadata = 0;
            fields[0] = fields[0].TrimStart('\r', '\n');
            if (fields[0].Length == 0) return null;
            if (count != 7) throw new CallFailed("Git returned invalid history metadata.");
            return new(fields[0], fields[1], fields[2], BoundedBody(fields[3]), fields[4], fields[5],
                fields[6].Split(", ", StringSplitOptions.RemoveEmptyEntries), fields[7].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private static string BoundedBody(string body)
    {
        if (Encoding.UTF8.GetByteCount(body) <= BodyBytes) return body;
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in body.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > BodyBytes - 3) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.Append('…').ToString();
    }
}
