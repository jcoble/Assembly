using System.Diagnostics;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class AdapterLauncherTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("assembly-launcher-").FullName;

    [Fact]
    public async Task A_helper_works_in_its_parent_s_folder_with_the_parent_s_uncommitted_files()
    {
        var repo = Path.Combine(_work, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--quiet");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "--quiet", "--allow-empty", "-m", "start");
        var launcher = new AdapterLauncher([], Path.Combine(_work, "checkouts"), new UnclaimedPermissions());
        var parent = new SessionInfo("parent", "claude", repo, null, null, 0);
        var helper = parent with { SessionId = "helper", Agent = "codex", ParentSessionId = "parent", Helper = true };

        var parentFolder = await launcher.CheckoutAsync(parent, new StageTimes(TimeProvider.System), CancellationToken.None);
        File.WriteAllText(Path.Combine(parentFolder, "notes.md"), "draft");
        var helperFolder = await launcher.CheckoutAsync(helper, new StageTimes(TimeProvider.System), CancellationToken.None);

        Assert.Equal(parentFolder, helperFolder);
        Assert.True(File.Exists(Path.Combine(helperFolder, "notes.md")));
    }

    [Fact]
    public void A_terminal_starts_in_a_local_session_s_checkout_and_a_remote_one_s_helper_can_t_open_here()
    {
        // Pins: terminal#1, terminal#12
        using var db = new TempDatabase();
        using var database = TestDatabase.Open(db.Path);
        var checkouts = Path.Combine(_work, "checkouts");
        var sessions = SessionStore.Open(database, TimeProvider.System, checkouts);
        var local = sessions.Create("claude", "https://github.com/o/r");
        var remote = sessions.Helper(sessions.Add("remote-1", "codex", "https://github.com/o/r", "Workbox Test"), "claude", "look");
        var folder = AdapterLauncher.ExistingFolder(checkouts, sessions);

        Assert.Null(folder(local.SessionId));
        Directory.CreateDirectory(Path.Combine(checkouts, local.SessionId));
        Assert.Equal(Path.Combine(checkouts, local.SessionId), folder(local.SessionId));
        Assert.Null(folder("unknown"));
        Assert.Contains("Workbox Test", Assert.Throws<CallFailed>(() => folder(remote.SessionId)).Message);
    }

    [Fact]
    public void The_old_checkouts_folder_moves_to_sessions_once()
    {
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_work, "threads", "s1")).FullName, "a.txt"), "kept");
        var log = new StringWriter();

        AdapterLauncher.MoveOldCheckouts(_work, Path.Combine(_work, "sessions"), log);
        AdapterLauncher.MoveOldCheckouts(_work, Path.Combine(_work, "sessions"), log);

        Assert.Equal("kept", File.ReadAllText(Path.Combine(_work, "sessions", "s1", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(_work, "threads")));
        Assert.Single(log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void When_both_folders_exist_both_are_left_as_they_are()
    {
        Directory.CreateDirectory(Path.Combine(_work, "threads", "old"));
        Directory.CreateDirectory(Path.Combine(_work, "sessions", "new"));
        var log = new StringWriter();

        AdapterLauncher.MoveOldCheckouts(_work, Path.Combine(_work, "sessions"), log);

        Assert.True(Directory.Exists(Path.Combine(_work, "threads", "old")));
        Assert.False(Directory.Exists(Path.Combine(_work, "sessions", "old")));
        Assert.Contains("both", log.ToString());
    }

    private static void Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var git = Process.Start(start)!;
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
    }

    public void Dispose() => Directory.Delete(_work, recursive: true);
}
