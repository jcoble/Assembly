using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>Normal fetch/fast-forward/push producers, invoked by the existing owned operation.</summary>
public sealed class GitSync(IGit git)
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);
    private const string Changed = "The branch changed or already has an upstream. Refresh Source Control and try again.";
    public Task<GitWriteResult> FetchAsync(string root, CancellationToken ct) => Run(root, "fetch", ["fetch", "--prune"], "Fetched remotes", ct);
    public async Task<GitWriteResult> PullAsync(string root, string expectedBranch, CancellationToken ct)
    {
        await Branch(root, expectedBranch, ct);
        return await Run(root, "pull", ["pull", "--ff-only"], "Pulled " + expectedBranch, ct);
    }
    public async Task<GitWriteResult> PushAsync(string root, string expectedBranch, CancellationToken ct)
    {
        await Branch(root, expectedBranch, ct);
        return await Run(root, "push", ["push"], "Pushed " + expectedBranch, ct);
    }
    public async Task<GitWriteResult> PublishAsync(string root, string expectedBranch, string remote, CancellationToken ct)
    {
        var status = await Branch(root, expectedBranch, ct);
        var configured = await git.RunAsync(root, ["config", "--get", "branch." + expectedBranch + ".merge"], null, null, ct);
        if (status.HasUpstream || configured.ExitCode == 0) throw new CallFailed(Changed);
        var remotes = await git.RunAsync(root, ["remote"], null, null, ct);
        if (remotes.ExitCode != 0) throw GitErrors.From("remote", remotes);
        if (!remotes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(remote, StringComparer.Ordinal)) throw new CallFailed(Changed);
        return await Run(root, "push", ["push", "--set-upstream", "--", remote, "HEAD:refs/heads/" + expectedBranch], "Published " + expectedBranch + " to " + remote, ct);
    }
    private async Task<GitStatus> Branch(string root, string expected, CancellationToken ct)
    {
        var status = await new GitStatusReader(git).ReadAsync(root, ct);
        if (status.Branch is null || status.Branch != expected) throw new CallFailed(Changed);
        return status;
    }
    private async Task<GitWriteResult> Run(string root, string verb, IReadOnlyList<string> arguments, string message, CancellationToken ct)
    {
        var run = await git.RunAsync(root, arguments, null, Limit, ct);
        if (run.ExitCode != 0) throw GitErrors.From(verb, run);
        return new(message, await new GitStatusReader(git).ReadAsync(root, ct));
    }
}
