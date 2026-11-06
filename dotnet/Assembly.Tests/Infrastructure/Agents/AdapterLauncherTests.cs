using System.Diagnostics;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Topics;
using Assembly.Tests.Engine;

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
        var checkouts = Path.Combine(_work, "historic-checkouts");
        using var f = new EngineFixture();
        var store = SessionStore.Open(f.Db, f.Time, checkouts);
        var launcher = new AdapterLauncher([], checkouts, new UnclaimedPermissions());
        await using var runtime = EngineRuntime.Start(f.Db, new Topics(), f.Time, f.Limits,
            cutover: new SessionCutover(f.Db, store, launcher, checkouts, f.Time));
        void Start(string id, string? parent) => Assert.True(f.Db.Writer.Write("test.recorded-parent-folder", scope =>
            runtime.Commands.Apply(scope, new(Guid.NewGuid(), runtime.Machine, new(ActorKind.Window, "owner"), id, 0, 0, 1000),
                new StartSession("codex", null, repo, id, parent, null, null))).Applied);
        Start("parent", null); Start("helper", "parent");
        var parentFolder = store.Root(store.Get("parent")!)!;
        launcher.ValidateFolder(parentFolder);
        await File.WriteAllTextAsync(Path.Combine(parentFolder, "notes.md"), "draft");
        var helperFolder = store.Root(store.Get("helper")!)!;
        launcher.ValidateFolder(helperFolder);

        Assert.Equal(repo, parentFolder);
        Assert.Equal(parentFolder, helperFolder);
        Assert.Equal("draft", await File.ReadAllTextAsync(Path.Combine(helperFolder, "notes.md")));
        Assert.False(Directory.Exists(checkouts)); // Reading/validating the actual existing checkout creates no clone root.
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
