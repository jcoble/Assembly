using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;

namespace Assembly.Application.Features.Chat.SourceControl;

public sealed record GitBranch(string Name, bool Current, string? Upstream, string? WorktreeFolder, string Subject);
public sealed record GitBranchList(IReadOnlyList<GitBranch> Branches, IReadOnlyList<string> Remotes, string? DefaultBranch);

/// <summary>Lists and creates branches. Session checkout remains the existing Engine operation.</summary>
public sealed class GitBranches(IGit git)
{
    public async Task<GitBranchList> ListAsync(string root, CancellationToken ct)
    {
        List<GitBranch> branches = [];
        await foreach (var record in git.ReadAsync(root, ["branch", "--list", "--sort=-committerdate", "--format=%(HEAD)%1f%(refname:short)%1f%(upstream:short)%1f%(worktreepath)%1f%(contents:subject)%00"], null, ct))
        {
            if (string.IsNullOrWhiteSpace(record)) continue;
            var fields = record.TrimStart('\r', '\n').Split('\u001f', 5);
            if (fields.Length != 5) throw new CallFailed("Git returned invalid branch metadata.");
            branches.Add(new(fields[1], fields[0] == "*", Empty(fields[2]), Empty(fields[3]), fields[4]));
        }
        var remotes = await git.RunAsync(root, ["remote"], null, null, ct);
        if (remotes.ExitCode != 0) throw GitErrors.From("remote", remotes);
        var current = await git.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], null, null, ct);
        var name = current.ExitCode == 0 ? current.Output.Trim() : null;
        if (name?.StartsWith("origin/", StringComparison.Ordinal) == true) name = name[7..];
        return new(branches, remotes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), name);
    }
    public async Task<GitWriteResult> CreateAsync(string root, string name, string? expectedHead, CancellationToken ct)
    {
        if (Refusal(name) is { } reason) throw new CallFailed(reason);
        name = name.Trim();
        var before = await new GitStatusReader(git).ReadAsync(root, ct);
        if (before.Head != expectedHead) throw new CallFailed("The branch moved since you looked. Refresh and try again.");
        var run = await git.RunAsync(root, ["branch", name], null, null, ct);
        if (run.ExitCode != 0) throw GitErrors.From("branch", run);
        return new("Created branch " + name, await new GitStatusReader(git).ReadAsync(root, ct));
    }
    public async Task<GitWriteResult> SwitchAsync(string root, string name, CancellationToken ct)
    {
        if (Refusal(name) is { } reason) throw new CallFailed(reason);
        name = name.Trim();
        var run = await git.RunAsync(root, ["switch", name], null, null, ct);
        if (run.ExitCode != 0) throw GitErrors.From("switch", run);
        return new("Switched to " + name, await new GitStatusReader(git).ReadAsync(root, ct));
    }
    public static string? Refusal(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Type a branch name first";
        if (name.StartsWith('-')) return "A branch name cannot start with a dash";
        if (Encoding.UTF8.GetByteCount(name) > 200) return "That branch name is too long";
        if (name.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || "~^:?*[\\".Contains(ch))) return "Branch names cannot contain spaces or any of ~ ^ : ? * [ \\";
        if (name.Contains("..", StringComparison.Ordinal) || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith(".lock", StringComparison.Ordinal)) return "That is not a name git will accept for a branch";
        return null;
    }
    private static string? Empty(string value) => value.Length == 0 ? null : value;
}
