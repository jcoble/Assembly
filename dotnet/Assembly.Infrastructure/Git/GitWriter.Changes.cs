using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Infrastructure.Git;

public sealed partial class GitWriter
{
    private const string Changed = "Files changed since you looked; review them again.";
    public async Task<GitWriteResult> StageAsync(string root, IReadOnlyList<string> paths, CancellationToken ct)
    {
        RequirePaths(paths);
        await Run(root, "add", ["--literal-pathspecs", "add", "--", .. paths], null, ct);
        return await Result(root, $"Staged {paths.Count} file(s)", ct);
    }
    public async Task<GitWriteResult> UnstageAsync(string root, IReadOnlyList<string> paths, CancellationToken ct)
    {
        RequirePaths(paths);
        var status = await new GitStatusReader(git).ReadAsync(root, ct);
        await Run(root, "restore", status.Head is null
            ? ["--literal-pathspecs", "rm", "--cached", "--", .. paths]
            : ["--literal-pathspecs", "restore", "--staged", "--", .. paths], null, ct);
        return await Result(root, $"Unstaged {paths.Count} file(s)", ct);
    }
    public async Task<GitWriteResult> StageAllAsync(string root, string expectedFingerprint, CancellationToken ct)
    {
        var full = await Current(root, expectedFingerprint, ct);
        var paths = Paths(full.All);
        if (paths.Count > 0) await Run(root, "add", ["--literal-pathspecs", "add", "--pathspec-from-file=-", "--pathspec-file-nul"], Stdin(paths), ct);
        return await Result(root, $"Staged {full.All.Count} file(s)", ct);
    }
    public async Task<GitWriteResult> UnstageAllAsync(string root, string expectedFingerprint, CancellationToken ct)
    {
        var full = await Current(root, expectedFingerprint, ct);
        var files = full.All.Where(file => file.Index is not ('.' or '?' or 'U')).ToArray(); // Restoring a conflict's index would erase it.
        var paths = Paths(files);
        if (paths.Count > 0) await Run(root, "restore", full.Status.Head is null
            ? ["--literal-pathspecs", "rm", "--cached", "--pathspec-from-file=-", "--pathspec-file-nul"]
            : ["--literal-pathspecs", "restore", "--staged", "--pathspec-from-file=-", "--pathspec-file-nul"], Stdin(paths), ct);
        return await Result(root, $"Unstaged {files.Length} file(s)", ct);
    }
    public async Task<GitWriteResult> DiscardAsync(string root, IReadOnlyList<string> tracked, IReadOnlyList<string> untracked,
        IReadOnlyList<ChangedFile> expected, CancellationToken ct)
    {
        RequirePaths([.. tracked, .. untracked]);
        var full = await new GitStatusReader(git).ReadFullAsync(root, ct);
        var current = full.All.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var prior = expected.ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (var path in tracked.Concat(untracked))
        {
            if (!current.TryGetValue(path, out var file) || !prior.TryGetValue(path, out var seen) || file != seen ||
                (file.Index == '?') != untracked.Contains(path, StringComparer.Ordinal)) throw new CallFailed(Changed);
        }
        await Discard(root, tracked.Select(path => current[path]).ToArray(), untracked, ct);
        return await Result(root, $"Discarded changes in {tracked.Count + untracked.Count} file(s)", ct);
    }
    public async Task<GitWriteResult> DiscardAllAsync(string root, string expectedFingerprint, bool includeUntracked, CancellationToken ct)
    {
        var full = await Current(root, expectedFingerprint, ct);
        var tracked = full.All.Where(file => file.Index != '?').ToArray();
        var untracked = includeUntracked ? full.All.Where(file => file.Index == '?').Select(file => file.Path).ToArray() : [];
        await Discard(root, tracked, untracked, ct);
        return await Result(root, $"Discarded changes in {tracked.Length + untracked.Length} file(s)", ct);
    }
    private async Task Discard(string root, IReadOnlyList<ChangedFile> tracked, IReadOnlyList<string> untracked, CancellationToken ct)
    {
        if (tracked.Count > 0) await Run(root, "restore",
            ["--literal-pathspecs", "restore", "--source=HEAD", "--staged", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul"], Stdin(Paths(tracked)), ct);
        foreach (var batch in untracked.Chunk(500))
            await Run(root, "clean", ["--literal-pathspecs", "clean", "-f", "-d", "--", .. batch], null, ct);
    }
    public async Task<GitWriteResult> CommitAsync(string root, string message, bool amend, string? expectedHead, CancellationToken ct)
    {
        if (message.Contains('\0') || (!amend && string.IsNullOrWhiteSpace(message))) throw new CallFailed("A commit message is required.");
        var full = await new GitStatusReader(git).ReadFullAsync(root, ct);
        if (full.Status.Head != expectedHead) throw new CallFailed("The branch moved since you looked. Refresh and try again.");
        var count = full.All.Count(file => file.Index is not ('.' or '?'));
        if (count == 0 && !amend) throw new CallFailed("There's nothing staged to commit.");
        List<string> args = ["commit"];
        if (amend) args.Add("--amend");
        if (amend && string.IsNullOrWhiteSpace(message)) args.Add("--no-edit");
        else args.AddRange(["-F", "-"]);
        await Run(root, "commit", args, args.Contains("-F") ? message : null, ct);
        return await Result(root, amend ? "Amended the last commit" : $"Committed {count} file(s)", ct);
    }
    public async Task<GitWriteResult> AddRemoteAsync(string root, string name, string url, CancellationToken ct)
    {
        name = name.Trim(); url = url.Trim();
        if (name.Length == 0 || url.Length == 0) throw new CallFailed("A remote name and URL are required.");
        if (name.StartsWith('-') || url.StartsWith('-')) throw new CallFailed("The remote name and URL cannot start with a hyphen.");
        await Run(root, "remote", ["remote", "add", "--", name, url], null, ct);
        return await Result(root, "Added remote " + name, ct);
    }
    private async Task Run(string root, string verb, IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        var run = await git.RunAsync(root, args, input, null, ct);
        if (run.ExitCode != 0) throw GitErrors.From(verb, run);
    }
    private async Task<GitWriteResult> Result(string root, string message, CancellationToken ct) =>
        new(message, await new GitStatusReader(git).ReadAsync(root, ct));
    private async Task<(GitStatus Status, IReadOnlyList<ChangedFile> All)> Current(string root, string fingerprint, CancellationToken ct)
    {
        var full = await new GitStatusReader(git).ReadFullAsync(root, ct);
        if (full.Status.Fingerprint != fingerprint) throw new CallFailed(Changed);
        return full;
    }
    private static IReadOnlyList<string> Paths(IEnumerable<ChangedFile> files) => files
        .SelectMany(file => file.OriginalPath is null ? new[] { file.Path } : new[] { file.Path, file.OriginalPath })
        .Distinct(StringComparer.Ordinal).ToArray();
    private static string Stdin(IEnumerable<string> paths) => string.Join('\0', paths) + "\0";
    private static void RequirePaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || paths.Any(path => string.IsNullOrEmpty(path) || path.Contains('\0')))
            throw new CallFailed("Choose files to change.");
    }
}
