using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Projects;

namespace Assembly.Infrastructure.Projects;

internal sealed partial class LocalProjectFolders
{
    public async Task<RefPage> RefsAsync(string root, string? checkoutPath, string? filter, string? after, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new CallBadArguments("limit must be between 1 and 100.");
        var inspection = await InspectAsync(root, ct);
        if (!inspection.IsGit) throw new CallBadArguments("This folder is not a Git repository.");
        var selected = checkoutPath is null ? inspection : await InspectAsync(checkoutPath, ct);
        if (selected.CommonDir != inspection.CommonDir) throw new CallBadArguments("That checkout belongs to another repository.");
        var records = new List<string>();
        await foreach (var record in git.ReadAsync(root, ["worktree", "list", "--porcelain", "-z"], null, ct)) records.Add(record);
        var trees = WorktreePorcelain.Parse(string.Join('\0', records));
        var held = trees.Where(t => t.Branch is not null).GroupBy(t => t.Branch!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.Ordinal);
        var refs = new List<GitRef>();
        await foreach (var record in git.ReadAsync(root,
            ["for-each-ref", "--sort=refname", "--format=%(refname)%00", "refs/heads", "refs/remotes", "refs/tags"], null, ct))
        {
            var full = record.Trim('\r', '\n');
            var kind = full.StartsWith("refs/heads/", StringComparison.Ordinal) ? "branch"
                : full.StartsWith("refs/remotes/", StringComparison.Ordinal) ? "remote" : "tag";
            var prefix = kind == "branch" ? "refs/heads/" : kind == "remote" ? "refs/remotes/" : "refs/tags/";
            if (!full.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var name = full;
            if ((filter is null || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                && (after is null || StringComparer.Ordinal.Compare(name, after) > 0))
                refs.Add(new(name, kind, kind == "branch" ? held.GetValueOrDefault(full[prefix.Length..]) : null));
        }
        // Canonical full refnames are unique across branch, remote and tag namespaces.
        var ordered = refs.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal).Take(limit + 1).ToList();
        var more = ordered.Count > limit;
        if (more) ordered.RemoveAt(limit);
        var branch = await TextAsync(selected.RootPath, ["symbolic-ref", "--short", "-q", "HEAD"], ct, optional: true);
        var head = await TextAsync(selected.RootPath, ["rev-parse", "--verify", "-q", "HEAD"], ct, optional: true);
        return new(ordered, new(branch, head), more ? ordered[^1].Name : null);
    }
}
