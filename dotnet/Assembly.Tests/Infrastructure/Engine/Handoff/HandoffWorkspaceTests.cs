using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Assembly.Infrastructure.Engine.Handoff;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

/// <summary>SOURCE-DERIVED workspace contract, exercised only against tiny isolated Git repositories.</summary>
public sealed class HandoffWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("assembly-handoff-git-").FullName;
    private string Checkouts => Path.Combine(_root, "checkouts");
    private const string Id = "a1234567-1111-4111-8111-111111111111";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_preserves_exact_fetch_url_and_different_remote_branch_name(bool fileUri)
    {
        var (source, upstream, head) = await Repo();
        await Git(source, "branch", "-m", "local-work");
        await Git(source, "remote", "rename", "origin", "actual-upstream");
        if (fileUri) upstream = new Uri(upstream).AbsoluteUri;
        await Git(source, "remote", "set-url", "actual-upstream", upstream);
        await Git(source, "fetch", "actual-upstream");
        var check = await HandoffWorkspace.CheckSourceAsync(source, default);
        Assert.Equal(new WorkspaceCheck(upstream, "published", head), check);
        var target = await HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, check, default);
        Assert.Equal(upstream, await Git(target, "remote", "get-url", "origin"));
        Assert.Equal(head, await Git(target, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Relative_upstream_resolves_from_real_worktree_root_including_nested_invocation()
    {
        var (source, upstream, head) = await Repo();
        var linked = Path.Combine(_root, "linked worktree");
        await Git(source, "worktree", "add", "-b", "local-work", linked, "published");
        await Git(linked, "config", "branch.local-work.remote", "origin");
        await Git(linked, "config", "branch.local-work.merge", "refs/heads/published");
        await Git(linked, "remote", "set-url", "origin", "../safe repo.git");
        var nested = Path.Combine(linked, "nested child");
        Directory.CreateDirectory(nested);
        // Actual Git fetch from the nested cwd establishes the identity independently of the helper.
        await Git(nested, "fetch", "origin");
        Assert.Equal(head, await Git(nested, "rev-parse", "@{upstream}"));
        var check = await HandoffWorkspace.CheckSourceAsync(nested, default);
        Assert.Equal(new WorkspaceCheck(upstream, "published", head), check);
        var target = await HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, check, default);
        Assert.Equal(head, await Git(target, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Caller_relative_repository_cannot_select_a_repo_from_target_staging()
    {
        var (_, upstream, head) = await Repo();
        Directory.CreateDirectory(Checkouts);
        // A real repo at the location a target-relative clone would accidentally select.
        await Git(Checkouts, "clone", "--bare", upstream, "safe repo.git");
        var untouched = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(Checkouts, untouched);
        await Refused("bad_args", () => HandoffWorkspace.PrepareTargetAsync(Checkouts, Id,
            new("../safe repo.git", "published", head), default));
        Assert.Equal(untouched, Directory.GetLastWriteTimeUtc(Checkouts));
        Assert.Equal(["safe repo.git"], Directory.GetFileSystemEntries(Checkouts).Select(Path.GetFileName));
        Assert.Equal(head, await Git(Path.Combine(Checkouts, "safe repo.git"), "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Valid_long_repository_name_keeps_the_repo_name_destination_contract()
    {
        var (source, upstream, head) = await Repo();
        var name = new string('r', 170);
        var renamed = Path.Combine(_root, name + ".git");
        Directory.Move(upstream, renamed);
        await Git(source, "remote", "set-url", "origin", renamed);
        var check = await HandoffWorkspace.CheckSourceAsync(source, default);
        var target = await HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, check, default);
        Assert.Equal(Path.Combine(Checkouts, name + "-a1234567"), target);
        Assert.Equal(head, await Git(target, "rev-parse", "HEAD"));
    }

    [Theory]
    [InlineData("tracked")]
    [InlineData("untracked")]
    [InlineData("conflict")]
    public async Task Source_refuses_all_uncommitted_work(string kind)
    {
        var (source, _, _) = await Repo();
        if (kind == "conflict")
        {
            await Git(source, "checkout", "-b", "other");
            await File.WriteAllTextAsync(Path.Combine(source, "file"), "other\n");
            await Commit(source);
            await Git(source, "checkout", "published");
            await File.WriteAllTextAsync(Path.Combine(source, "file"), "main\n");
            await Commit(source);
            await Git(source, ["merge", "other"], expectedExit: 1);
        }
        else await File.WriteAllTextAsync(Path.Combine(source, kind == "tracked" ? "file" : "new file"), "changed");
        await Refused("uncommitted_changes", () => HandoffWorkspace.CheckSourceAsync(source, default));
    }

    [Theory]
    [InlineData("detached")]
    [InlineData("no-upstream")]
    [InlineData("ahead")]
    [InlineData("behind")]
    [InlineData("missing-ref")]
    [InlineData("multiple-merge")]
    [InlineData("multiple-remote")]
    [InlineData("local-upstream")]
    public async Task Source_refuses_unpublished_or_ambiguous_branch(string kind)
    {
        var (source, _, head) = await Repo();
        switch (kind)
        {
            case "detached": await Git(source, "checkout", "--detach"); break;
            case "no-upstream": await Git(source, "branch", "--unset-upstream"); break;
            case "ahead": await Git(source, "commit", "--allow-empty", "-m", "ahead"); break;
            case "behind":
                await Git(source, "commit", "--allow-empty", "-m", "ahead");
                await Git(source, "push");
                await Git(source, "update-ref", "refs/heads/published", head);
                break;
            case "missing-ref": await Git(source, "update-ref", "-d", "refs/remotes/origin/published"); break;
            case "multiple-merge": await Git(source, "config", "--add", "branch.published.merge", "refs/heads/other"); break;
            case "multiple-remote": await Git(source, "config", "--add", "branch.published.remote", "another"); break;
            case "local-upstream": await Git(source, "config", "branch.published.remote", "."); break;
        }
        await Refused("branch_not_pushed", () => HandoffWorkspace.CheckSourceAsync(source, default));
    }

    [Fact]
    public async Task Source_refuses_nonrepository_and_unborn_branch()
    {
        await Refused("not_a_git_checkout", () => HandoffWorkspace.CheckSourceAsync(_root, default));
        var unborn = Path.Combine(_root, "unborn");
        Directory.CreateDirectory(unborn);
        await Git(unborn, "init", "-b", "main");
        await Refused("branch_not_pushed", () => HandoffWorkspace.CheckSourceAsync(unborn, default));
    }

    [Fact]
    public async Task Target_publishes_exact_commit_and_reuses_only_matching_owned_checkout()
    {
        var (source, upstream, head) = await Repo();
        var check = await HandoffWorkspace.CheckSourceAsync(source, default);
        var target = await HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, check, default);
        Assert.Equal(Path.Combine(Checkouts, "safe-repo-a1234567"), target);
        Assert.Equal(head, await Git(target, "rev-parse", "HEAD"));
        Assert.Equal("commit", await Git(target, "cat-file", "-t", head));
        Assert.Equal("published", await Git(target, "branch", "--show-current"));
        Assert.Equal(upstream, await Git(target, "remote", "get-url", "origin"));
        Assert.Equal("", await Git(target, "status", "--porcelain", "--untracked-files=all"));
        Assert.Equal("initial\n", await File.ReadAllTextAsync(Path.Combine(target, "file")));
        Assert.Equal(target, await HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, check, default));
        Assert.Single(Directory.GetDirectories(Checkouts));
    }

    [Theory]
    [InlineData("missing-object")]
    [InlineData("different-commit")]
    [InlineData("wrong-branch")]
    [InlineData("not-commit")]
    public async Task Target_refuses_unadvertised_or_noncommit_head_without_publication(string kind)
    {
        var (source, upstream, head) = await Repo();
        var branch = "published";
        if (kind == "missing-object") head = new string('1', 40);
        if (kind == "different-commit")
        {
            await Git(source, "commit", "--allow-empty", "-m", "new");
            await Git(source, "push");
        }
        if (kind == "wrong-branch") branch = "absent";
        if (kind == "not-commit") head = await Git(source, "rev-parse", "HEAD:file");
        await Refused("head_not_found", () => HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, new(upstream, branch, head), default));
        Assert.Empty(Directory.GetFileSystemEntries(Checkouts));
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("branch")]
    [InlineData("head")]
    [InlineData("repository")]
    [InlineData("foreign-owner")]
    public async Task Target_never_overwrites_existing_checkout_conflicts(string kind)
    {
        var (source, upstream, head) = await Repo();
        var check = new WorkspaceCheck(upstream, "published", head);
        var target = await HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, check, default);
        if (kind == "dirty") await File.WriteAllTextAsync(Path.Combine(target, "precious"), "keep");
        if (kind == "branch") await Git(target, "checkout", "-b", "different");
        if (kind == "head") await Git(target, "commit", "--allow-empty", "-m", "unmerged work");
        if (kind == "repository") await Git(target, "remote", "set-url", "origin", upstream + "-different");
        var requestedId = kind == "foreign-owner" ? "a1234567-2222-4222-8222-222222222222" : Id;
        var beforeHead = await Git(target, "rev-parse", "HEAD");
        var beforeStatus = await Git(target, "status", "--porcelain", "--untracked-files=all");
        await Refused("bad_args", () => HandoffWorkspace.PrepareTargetAsync(Checkouts, requestedId, check, default));
        Assert.Equal(beforeHead, await Git(target, "rev-parse", "HEAD"));
        Assert.Equal(beforeStatus, await Git(target, "status", "--porcelain", "--untracked-files=all"));
        if (kind == "dirty") Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "precious")));
    }

    [Fact]
    public async Task Existing_unowned_final_and_partial_are_preserved()
    {
        var (_, upstream, head) = await Repo();
        var final = Path.Combine(Checkouts, "safe-repo-a1234567");
        Directory.CreateDirectory(final);
        await File.WriteAllTextAsync(Path.Combine(final, "precious"), "final");
        await Refused("bad_args", () => HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, new(upstream, "published", head), default));
        Assert.Equal("final", await File.ReadAllTextAsync(Path.Combine(final, "precious")));
        Directory.Delete(final, recursive: true);
        Directory.CreateDirectory(final + ".partial");
        await File.WriteAllTextAsync(Path.Combine(final + ".partial", "precious"), "partial");
        await Refused("bad_args", () => HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, new(upstream, "published", head), default));
        Assert.Equal("partial", await File.ReadAllTextAsync(Path.Combine(final + ".partial", "precious")));
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("padded-uuid")]
    [InlineData("branch")]
    [InlineData("oid")]
    [InlineData("checkout-link")]
    [InlineData("ancestor-link")]
    [InlineData("final-link")]
    public async Task Invalid_input_and_links_refuse_without_touching_external_files(string kind)
    {
        var (_, upstream, head) = await Repo();
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "precious"), "keep");
        var checkouts = Checkouts;
        if (kind == "checkout-link") Directory.CreateSymbolicLink(checkouts, external);
        if (kind == "ancestor-link")
        {
            Directory.CreateSymbolicLink(Checkouts, external);
            checkouts = Path.Combine(Checkouts, "nested");
        }
        if (kind == "final-link")
        {
            Directory.CreateDirectory(Checkouts);
            Directory.CreateSymbolicLink(Path.Combine(Checkouts, "safe-repo-a1234567"), external);
        }
        await Refused("bad_args", () => HandoffWorkspace.PrepareTargetAsync(checkouts, kind == "uuid" ? "../escape" : kind == "padded-uuid" ? " " + Id + " " : Id,
            new(upstream, kind == "branch" ? "../bad" : "published", kind == "oid" ? "--help" : head), default));
        Assert.Equal(["precious"], Directory.GetFileSystemEntries(external).Select(Path.GetFileName));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(external, "precious")));
    }

    [Fact]
    public async Task Cancellation_waits_for_git_disconnect_and_removes_only_owned_staging()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cts = new CancellationTokenSource();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var pending = HandoffWorkspace.PrepareTargetAsync(Checkouts, Id,
                new($"git://127.0.0.1:{port}/safe.git", "published", new string('1', 40)), cts.Token);
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(Directory.Exists(Path.Combine(Checkouts, "safe-a1234567")));
            var stream = client.GetStream();
            var request = new byte[4096];
            Assert.True(await stream.ReadAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) > 0);
            await Refused("bad_args", () => HandoffWorkspace.PrepareTargetAsync(Checkouts,
                "a1234567-2222-4222-8222-222222222222",
                new($"git://127.0.0.1:{port}/safe.git", "published", new string('1', 40)), default));
            Assert.True(Directory.Exists(Path.Combine(Checkouts, "safe-a1234567.partial")));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, await stream.ReadAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(Directory.GetFileSystemEntries(Checkouts));
        }
        finally { cts.Cancel(); listener.Stop(); }
    }

    [Fact]
    public async Task Precancelled_retry_keeps_committed_target_unchanged()
    {
        var (_, upstream, head) = await Repo();
        var check = new WorkspaceCheck(upstream, "published", head);
        var target = await HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, check, default);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, check, cts.Token));
        Assert.Equal(head, await Git(target, "rev-parse", "HEAD"));
        Assert.Equal("initial\n", await File.ReadAllTextAsync(Path.Combine(target, "file")));
    }

    private async Task<(string Source, string Upstream, string Head)> Repo()
    {
        var upstream = Path.Combine(_root, "safe repo.git");
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(upstream);
        Directory.CreateDirectory(source);
        await Git(upstream, "init", "--bare", "--initial-branch=published");
        await Git(source, "init", "--initial-branch=published");
        await File.WriteAllTextAsync(Path.Combine(source, "file"), "initial\n");
        await Commit(source);
        await Git(source, "remote", "add", "origin", upstream);
        await Git(source, "push", "--set-upstream", "origin", "published");
        return (source, upstream, await Git(source, "rev-parse", "HEAD"));
    }

    private static async Task<string> Commit(string folder)
    {
        await Git(folder, "add", "-A");
        return await Git(folder, "commit", "-m", "fixture");
    }

    private static async Task Refused(string reason, Func<Task> action) =>
        Assert.Equal(reason, (await Assert.ThrowsAsync<HandoffRefused>(action)).Message);

    private static Task<string> Git(string folder, params string[] arguments) => Git(folder, arguments, 0);
    private static async Task<string> Git(string folder, string[] arguments, int expectedExit)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false", "-c", "user.name=Handoff Fixture", "-c", "user.email=handoff@example.invalid" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == expectedExit, await stderr);
        return (await stdout).TrimEnd('\r', '\n');
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
