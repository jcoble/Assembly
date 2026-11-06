using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>A commit's literal changed paths and patch, comparing merges to their first parent.</summary>
public sealed class GitCommitReader(IGit git)
{
    public async Task<IReadOnlyList<GitCommitFile>> FilesAsync(string root, string sha, CancellationToken ct)
    {
        ValidateSha(sha);
        await using var records = git.ReadAsync(root,
            ["--literal-pathspecs", "show", "--name-status", "-M", "--diff-merges=first-parent", "--format=", "-z", sha], null, ct).GetAsyncEnumerator(ct);
        List<GitCommitFile> files = [];
        while (await records.MoveNextAsync())
        {
            var status = records.Current.TrimStart('\r', '\n');
            if (status.Length == 0) continue;
            if (!await records.MoveNextAsync()) throw new CallFailed("Git returned incomplete commit paths.");
            var first = records.Current;
            if (status[0] is 'R' or 'C')
            {
                if (!await records.MoveNextAsync()) throw new CallFailed("Git returned incomplete commit paths.");
                files.Add(new(records.Current, first, status[0]));
            }
            else files.Add(new(first, null, status[0]));
        }
        return files;
    }

    public async Task<GitFileDiff> DiffAsync(string root, string sha, string path, CancellationToken ct)
    {
        ValidateSha(sha);
        var diff = await GitDiffReader.ReadText(git, root,
            ["--literal-pathspecs", "-c", "core.quotepath=false", "show", "--no-ext-diff", "--no-color", "-M",
                "--diff-merges=first-parent", "--format=", sha, "--", path], GitDiffReader.Cap, ct);
        var binary = diff.Text.StartsWith("Binary files ", StringComparison.Ordinal) || diff.Text.Contains("\nBinary files ", StringComparison.Ordinal);
        return new(path, "", binary ? "" : diff.Text, binary, diff.Truncated);
    }

    private static void ValidateSha(string sha)
    {
        if (sha.Length is < 7 or > 40 || sha.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new CallFailed("Expected a Git commit SHA.");
    }
}
