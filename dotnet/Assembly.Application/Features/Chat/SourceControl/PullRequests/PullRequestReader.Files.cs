using Assembly.Application.Common.Calls;
namespace Assembly.Application.Features.Chat.SourceControl.PullRequests;

public sealed partial class PullRequestReader
{
    public async Task<PrFileContext> FileAsync(string root, int number, string path, string baseSha, string headSha, CancellationToken ct)
    {
        ValidatePath(path); ValidateRevision(baseSha); ValidateRevision(headSha);
        var detail = await DetailAsync(root, number, ct);
        if (detail.BaseSha != baseSha || detail.HeadSha != headSha) throw new CallFailed("This pull request changed. Refresh it before reading the file.");
        var file = detail.Files.FirstOrDefault(file => file.Path == path) ?? throw new CallFailed("That file is not in this pull request.");
        var oldPath = file.PreviousPath ?? file.Path; ValidatePath(oldPath);
        var before = file.Status == "added" ? null : await Blob(root, detail.Repository, oldPath, baseSha, ct);
        var after = file.Status == "removed" ? null : await Blob(root, detail.Repository, file.Path, headSha, ct);
        var patch = await patches.ReadAsync(path, oldPath, before, after, ct);
        return new(path, patch.Unstaged, patch.Binary, patch.Truncated);
    }
    private async Task<ReadOnlyMemory<byte>> Blob(string root, string repository, string path, string sha, CancellationToken ct)
    {
        var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var run = await gh.RunAsync(root, ["api", "-H", "Accept: application/vnd.github.raw+json", $"repos/{repository}/contents/{encoded}?ref={sha}"], null, Limit, ct);
        if (run.ExitCode != 0) throw new CallFailed("GitHub file read failed.\n\n" + run.Error.Trim());
        return run.OutputBytes ?? throw new CallFailed("GitHub's runner did not supply the exact file bytes.");
    }
    private void ValidatePath(string path) => patches.ValidatePath(path);
    private static void ValidateRevision(string sha)
    {
        if (sha.Length != 40 || sha.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new CallFailed("Invalid pull request revision.");
    }
}
