using System.Diagnostics;
using System.Globalization;

namespace Assembly.DevSeed;

public sealed record SeedCommit(string Message, double DaysAgo, IReadOnlyDictionary<string, string> Files);
public sealed record SeedBranch(string Name, bool Worktree, IReadOnlyList<SeedCommit> Commits, IReadOnlyDictionary<string, string>? Dirty = null);
public sealed record SeedRepo(string Key, string Title, IReadOnlyList<SeedCommit> Commits, IReadOnlyList<SeedBranch> Branches,
    IReadOnlyDictionary<string, string> Staged, IReadOnlyDictionary<string, string> Modified, bool Origin = false);

/// <summary>Real small Git repositories. Git runs with no user config, no hooks and no transports but local files.</summary>
public static class SeedRepos
{
    public static void Build(SeedWorld world, IReadOnlyList<SeedRepo> repos, CancellationToken cancel)
    {
        foreach (var repo in repos)
        {
            cancel.ThrowIfCancellationRequested();
            var root = world.Project(repo.Key);
            Directory.CreateDirectory(root);
            Git(world, root, null, "init", "-q", "-b", "main");
            foreach (var commit in repo.Commits) Commit(world, root, commit);
            foreach (var branch in repo.Branches)
            {
                Git(world, root, null, "branch", branch.Name);
                var folder = root;
                if (branch.Worktree)
                {
                    folder = world.Worktree(repo.Key, branch.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                    Git(world, root, null, "worktree", "add", "-q", folder, branch.Name);
                }
                else Git(world, root, null, "switch", "-q", branch.Name);
                foreach (var commit in branch.Commits) Commit(world, folder, commit);
                if (!branch.Worktree) Git(world, root, null, "switch", "-q", "main");
                if (branch.Dirty is { } dirty) Files(folder, dirty);
            }
            if (repo.Origin)
            {
                var origin = Path.Combine(world.Home, "projects", ".origin", repo.Key + ".git");
                Directory.CreateDirectory(origin);
                Git(world, origin, null, "init", "-q", "--bare", "-b", "main");
                Git(world, root, null, "remote", "add", "origin", origin);
                // Everything but the newest commit, so the branch reads as ahead.
                Git(world, root, null, "push", "-q", "origin", "HEAD~1:refs/heads/main");
                Git(world, root, null, "fetch", "-q", "origin");
                Git(world, root, null, "branch", "-q", "--set-upstream-to=origin/main", "main");
            }
            Files(root, repo.Staged);
            if (repo.Staged.Count > 0) Git(world, root, null, ["add", "--", .. repo.Staged.Keys]);
            Files(root, repo.Modified);
        }
    }

    private static void Commit(SeedWorld world, string folder, SeedCommit commit)
    {
        Files(folder, commit.Files);
        Git(world, folder, null, "add", "-A");
        Git(world, folder, world.Ago(commit.DaysAgo * 1440), "commit", "-q", "-m", commit.Message);
    }

    private static void Files(string folder, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (relative, text) in files)
        {
            var path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    private static void Git(SeedWorld world, string folder, long? atMs, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var option in new[] { "-c", "core.hooksPath=/dev/null", "-c", "protocol.allow=never", "-c", "protocol.file.allow=always", "-c", "commit.gpgsign=false", "-c", "gc.auto=0" })
            start.ArgumentList.Add(option);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var at = (atMs ?? world.NowMs) / 1000;
        var date = at.ToString(CultureInfo.InvariantCulture) + " +0000";
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_AUTHOR_NAME"] = "Dev Seed",
            ["GIT_AUTHOR_EMAIL"] = "seed@example.invalid",
            ["GIT_AUTHOR_DATE"] = date,
            ["GIT_COMMITTER_NAME"] = "Dev Seed",
            ["GIT_COMMITTER_EMAIL"] = "seed@example.invalid",
            ["GIT_COMMITTER_DATE"] = date,
        }) start.Environment[name] = value;
        using var git = Process.Start(start) ?? throw new IOException("git did not start.");
        var errors = git.StandardError.ReadToEndAsync();
        git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new IOException($"git {string.Join(' ', args)} failed: {errors.Result}");
    }
}
