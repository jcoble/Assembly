using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;

namespace Assembly.Infrastructure.Engine.Workflows;

internal static class WorkflowManagedRoot
{
    public static async Task<string> ResolveAsync(string trustedProjectRoot, IGit git, CancellationToken ct)
    {
        List<string> values = [];
        await foreach (var value in git.ReadAsync(trustedProjectRoot,
            ["rev-parse", "--path-format=absolute", "--git-common-dir"], null, ct)) values.Add(value);
        var common = string.Concat(values).TrimEnd('\r', '\n');
        if (!Path.IsPathFullyQualified(common)) throw new CallFailed("invalid_git_common_directory");
        common = WorkflowPaths.Canonical(common);
        var repository = Path.GetFileName(common) == ".git" ? Path.GetDirectoryName(common)! : common;
        var parent = Path.GetDirectoryName(repository) ?? throw new CallFailed("invalid_git_common_directory");
        return WorkflowPaths.Canonical(Path.Combine(parent, "worktrees"));
    }
}
