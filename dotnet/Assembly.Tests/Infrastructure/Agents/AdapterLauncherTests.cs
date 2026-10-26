using System.Diagnostics;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

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
        var parent = new ThreadInfo("parent", "claude", repo, null, null, 0);
        var helper = parent with { ThreadId = "helper", Agent = "codex", ParentThreadId = "parent", Helper = true };

        var parentFolder = await launcher.CheckoutAsync(parent, new StageTimes(TimeProvider.System), CancellationToken.None);
        File.WriteAllText(Path.Combine(parentFolder, "notes.md"), "draft");
        var helperFolder = await launcher.CheckoutAsync(helper, new StageTimes(TimeProvider.System), CancellationToken.None);

        Assert.Equal(parentFolder, helperFolder);
        Assert.True(File.Exists(Path.Combine(helperFolder, "notes.md")));
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
