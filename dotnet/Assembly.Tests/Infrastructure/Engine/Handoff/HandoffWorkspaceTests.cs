using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Assembly.Application.Common.Calls;
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
    public async Task Source_preserves_ssh_username_identity(bool scp)
    {
        var (source, _, head) = await Repo();
        var url = scp ? "git@host.invalid:org/repo.git" : "ssh://git@host.invalid/org/repo.git";
        await Git(source, "remote", "set-url", "origin", url);
        Assert.Equal(new WorkspaceCheck(url, "published", head), await HandoffWorkspace.CheckSourceAsync(source, default));
    }

    [Theory]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("https", false)]
    [InlineData("https", true)]
    public async Task Source_refuses_http_userinfo_before_returning_a_workspace(string scheme, bool password)
    {
        var (source, _, _) = await Repo();
        // Dummy values only; source inspection never connects to this URL.
        var user = password ? "fixture-user:dummy-not-secret" : "dummy-not-secret";
        await Git(source, "remote", "set-url", "origin", scheme + "://" + user + "@host.invalid/org/repo.git");
        await Refused("bad_args", () => HandoffWorkspace.CheckSourceAsync(source, default));
        Assert.False(Directory.Exists(Checkouts));
    }

    [Theory]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("https", false)]
    [InlineData("https", true)]
    public async Task Target_refuses_http_userinfo_before_any_filesystem_change(string scheme, bool password)
    {
        var (_, _, head) = await Repo();
        var user = password ? "fixture-user:dummy-not-secret" : "dummy-not-secret";
        // .git has an empty sanitized basename: the old helper fails locally after creating checkouts,
        // so this regression proves early credential refusal without making any network request.
        var url = scheme + "://" + user + "@host.invalid/.git";
        await Refused("bad_args", () => HandoffWorkspace.PrepareTargetAsync(Checkouts, Id,
            new(url, "published", head), default));
        Assert.False(Directory.Exists(Checkouts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_source_repository_preserves_the_actual_git_failure(bool nonRepository)
    {
        var (source, upstream, _) = await Repo();
        var check = await HandoffWorkspace.CheckSourceAsync(source, default);
        Directory.Move(upstream, upstream + ".offline");
        if (nonRepository)
        {
            Directory.CreateDirectory(upstream);
            await File.WriteAllTextAsync(Path.Combine(upstream, "precious"), "keep");
        }
        var error = await Assert.ThrowsAsync<CallFailed>(() => HandoffWorkspace.PrepareTargetAsync(Checkouts, Id, check, default));
        Assert.Contains("failed:", error.Message);
        Assert.Contains(upstream, error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Checkouts));
        if (nonRepository) Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(upstream, "precious")));
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
