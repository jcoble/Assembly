using Assembly.Application.Common.Git;
using Assembly.Application.Common.Calls;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>The same full porcelain stream supplies both the display cap and the mutation fingerprint. Each file also
/// gets its revision, and the fingerprint covers them, because an edit to an already changed file keeps its letters.
/// Up to 1000 changed files also carry per-side numstat line counts, and branches off the default base carry that base
/// and the timestamp of the branch's first own commit.</summary>
public sealed class GitStatusReader(IGit git)
{
    private const int MaxFiles = 1000;

    public async Task<GitStatus> ReadAsync(string root, CancellationToken ct) => (await ReadFullAsync(root, ct)).Status;

    public async Task<(GitStatus Status, IReadOnlyList<ChangedFile> All)> ReadFullAsync(string root, CancellationToken ct)
    {
        var full = await GitStatusPorcelain.ParseAsync(Records(root, ct), int.MaxValue);
        var all = full.Files.Select(file => file with { Revision = git.Revision(root, file.Path) }).ToArray();
        var fingerprint = Fingerprint(full.Fingerprint, all);
        if (all.Length > 0 && all.Length <= MaxFiles)
            all = await AttachCountsAsync(root, all, ct);
        var (baseBranch, branchCreatedAt) = full.Branch is not null && full.Head is not null
            ? await ReadBranchOriginAsync(root, full.Branch, ct)
            : (null, null);
        return (full with
        {
            Files = all.Take(MaxFiles).ToArray(),
            Fingerprint = fingerprint,
            CheckoutRoot = Path.GetFullPath(root),
            BaseBranch = baseBranch,
            BranchCreatedAt = branchCreatedAt
        }, all);
    }

    private async Task<ChangedFile[]> AttachCountsAsync(string root, ChangedFile[] files, CancellationToken ct)
    {
        var hasStaged = files.Any(file => file.Index is not ('.' or '?' or 'U') && file.Worktree != 'U');
        var hasUnstaged = files.Any(file => file.Worktree is not ('.' or '?' or 'U') && file.Index != 'U');
        var staged = hasStaged ? await ReadNumstatAsync(root, ["diff", "--cached", "--numstat", "--no-ext-diff", "-z", "-M"], ct) : null;
        var unstaged = hasUnstaged ? await ReadNumstatAsync(root, ["diff", "--numstat", "--no-ext-diff", "-z"], ct) : null;
        if (staged is not { Count: > 0 } && unstaged is not { Count: > 0 })
            return files;
        return files.Select(file =>
        {
            (int Added, int Removed)? s = staged is not null && staged.TryGetValue(file.Path, out var stagedCount) ? stagedCount : null;
            (int Added, int Removed)? u = unstaged is not null && unstaged.TryGetValue(file.Path, out var unstagedCount) ? unstagedCount : null;
            return s is not null || u is not null
                ? file with
                {
                    StagedAdded = s?.Added,
                    StagedRemoved = s?.Removed,
                    UnstagedAdded = u?.Added,
                    UnstagedRemoved = u?.Removed
                }
                : file;
        }).ToArray();
    }

    private async Task<Dictionary<string, (int Added, int Removed)>> ReadNumstatAsync(string root, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var counts = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        try
        {
            await using var records = git.ReadAsync(root, arguments, null, ct).GetAsyncEnumerator(ct);
            while (await records.MoveNextAsync())
            {
                var record = records.Current;
                if (record.Length == 0) continue;
                var tab1 = record.IndexOf('\t');
                if (tab1 < 0) continue;
                var tab2 = record.IndexOf('\t', tab1 + 1);
                if (tab2 < 0) continue;
                var path = record[(tab2 + 1)..];
                if (path.Length == 0)
                {
                    // With -z, renames/copies emit "added\tremoved\t\0old\0new\0".
                    if (!await records.MoveNextAsync() || !await records.MoveNextAsync())
                        break;
                    path = records.Current;
                }
                if (int.TryParse(record.AsSpan(0, tab1), NumberStyles.None, CultureInfo.InvariantCulture, out var added)
                    && int.TryParse(record.AsSpan(tab1 + 1, tab2 - tab1 - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var removed))
                    counts[path] = (added, removed);
            }
        }
        catch (CallFailed) when (!ct.IsCancellationRequested)
        {
        }
        return counts;
    }

    private async Task<(string? BaseBranch, long? BranchCreatedAt)> ReadBranchOriginAsync(string root, string branch, CancellationToken ct)
    {
        try
        {
            var resolved = await ResolveBaseBranchAsync(root, ct);
            if (resolved is not { } basis || string.Equals(branch, basis.Name, StringComparison.Ordinal))
                return (null, null);
            var mergeBase = await git.RunAsync(root, ["merge-base", "HEAD", basis.Ref], null, null, ct);
            if (mergeBase.ExitCode != 0)
                return (basis.Name, null);
            var sha = mergeBase.Output.Trim();
            if (sha.Length == 0)
                return (basis.Name, null);
            var commits = await git.RunAsync(root, ["log", "--reverse", "--no-show-signature", "--format=%ct", $"{sha}..HEAD"], null, null, ct);
            if (commits.ExitCode != 0)
                return (basis.Name, null);
            var firstLine = commits.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return long.TryParse(firstLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? (basis.Name, seconds * 1000)
                : (basis.Name, null);
        }
        catch (CallFailed) when (!ct.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    private async Task<(string Ref, string Name)?> ResolveBaseBranchAsync(string root, CancellationToken ct)
    {
        var origin = await git.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], null, null, ct);
        if (origin.ExitCode == 0)
        {
            var value = origin.Output.Trim();
            if (value.Length > 0)
            {
                var name = value.StartsWith("origin/", StringComparison.Ordinal) ? value[7..] : value;
                if (name.Length > 0)
                    return (value, name);
            }
        }
        foreach (var candidate in new[] { "main", "master" })
        {
            var reference = "refs/heads/" + candidate;
            var check = await git.RunAsync(root, ["rev-parse", "--verify", "--quiet", reference], null, null, ct);
            if (check.ExitCode == 0)
                return (reference, candidate);
        }
        return null;
    }

    private static string Fingerprint(string records, IEnumerable<ChangedFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(records));
        foreach (var file in files) hash.AppendData(Encoding.UTF8.GetBytes($"\0{file.Path}\0{file.Revision}"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private async IAsyncEnumerable<string> Records(string root, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var records = git.ReadAsync(root, ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"], null, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try { more = await records.MoveNextAsync(); }
            catch (CallFailed failed) { throw GitErrors.From("status", failed.Message); }
            if (!more) yield break;
            yield return records.Current;
        }
    }
}
