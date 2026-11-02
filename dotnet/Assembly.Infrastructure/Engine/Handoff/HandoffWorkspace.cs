using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.SourceControl;

namespace Assembly.Infrastructure.Engine.Handoff;

public sealed record WorkspaceCheck(string RepoUrl, string Branch, string Head);
public sealed class HandoffRefused(string reason) : Exception(reason);

/// <summary>Checks the real upstream identity and prepares a separately owned exact-commit checkout.</summary>
public static class HandoffWorkspace
{
    public static async Task<WorkspaceCheck> CheckSourceAsync(string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        GitStatus status;
        try
        {
            if (await HandoffGit.Scalar(folder, ["rev-parse", "--is-inside-work-tree"], ct) != "true")
                throw new HandoffRefused("not_a_git_checkout");
            status = await new GitStatusReader(HandoffGit.Cli).ReadAsync(folder, ct);
        }
        catch (CallFailed) { throw new HandoffRefused("not_a_git_checkout"); }
        if (status.TotalFiles != 0) throw new HandoffRefused("uncommitted_changes");
        if (!HandoffGit.IsBranch(status.Branch) || !HandoffGit.IsOid(status.Head) || !status.HasUpstream ||
            status.Ahead != 0 || status.Behind != 0)
            throw new HandoffRefused("branch_not_pushed");
        try
        {
            var branch = status.Branch!;
            if (await HandoffGit.Scalar(folder, ["symbolic-ref", "--quiet", "HEAD"], ct) != "refs/heads/" + branch)
                throw new HandoffRefused("branch_not_pushed");
            var remote = await Config(folder, "branch." + branch + ".remote", ct);
            var merge = await Config(folder, "branch." + branch + ".merge", ct);
            if (remote is null or "." || merge is null || !merge.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                !HandoffGit.IsBranch(merge[11..])) throw new HandoffRefused("branch_not_pushed");
            // An absent/ambiguous ref can return no output with GitCli's exit-1 semantics. Equality is required.
            var upstream = await HandoffGit.Scalar(folder, ["rev-parse", "--verify", "@{upstream}^{commit}"], ct);
            if (upstream != status.Head || !HandoffGit.IsOid(upstream)) throw new HandoffRefused("branch_not_pushed");
            if (await Config(folder, "remote." + remote + ".url", ct) is null)
                throw new HandoffRefused("branch_not_pushed");
            // get-url resolves the configured fetch URL exactly as Git would, including a configured insteadOf rule.
            var url = await HandoffGit.Scalar(folder, ["remote", "get-url", "--all", remote], ct);
            if (!HandoffGit.IsUrl(url)) throw new HandoffRefused("branch_not_pushed");
            // Controller B mapping: relative filesystem remotes are resolved from Git's actual worktree root,
            // including linked worktrees and calls made inside a nested folder. Transport/host aliases stay exact.
            if (HandoffGit.IsRelativeFileUrl(url))
            {
                var root = await HandoffGit.Scalar(folder, ["rev-parse", "--show-toplevel"], ct);
                if (!Path.IsPathFullyQualified(root)) throw new HandoffRefused("branch_not_pushed");
                url = Path.GetFullPath(url, root);
            }
            return new WorkspaceCheck(url, merge[11..], upstream);
        }
        catch (CallFailed) { throw new HandoffRefused("branch_not_pushed"); }
    }

    public static Task<string> PrepareTargetAsync(string checkouts, string handoffId, WorkspaceCheck source, CancellationToken ct) =>
        HandoffCheckout.PrepareAsync(checkouts, handoffId, source, ct);

    private static async Task<string?> Config(string folder, string key, CancellationToken ct)
    {
        var values = await HandoffGit.Records(folder, ["config", "--null", "--get-all", key], ct);
        return values.Count == 1 && values[0].Length > 0 ? values[0] : null;
    }
}
