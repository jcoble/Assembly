using System.Diagnostics;

namespace Assembly.Tests.Draft.Files;

/// <summary>A temp git repo with the given files (paths with '/'; a path ending in '/' is an empty folder), removed on dispose.</summary>
internal sealed class GitFolder : IDisposable
{
    public string Root { get; }

    public GitFolder(params string[] paths)
    {
        Root = Directory.CreateTempSubdirectory("files-").FullName;
        Fill(Root, paths);
    }

    /// <summary>Makes <paramref name="root"/> a git repo with those files, leaving it in place.</summary>
    public static void Fill(string root, params string[] paths)
    {
        Directory.CreateDirectory(root);
        Run(root, "init", "--quiet");
        foreach (var path in paths)
        {
            var full = Path.Combine(root, path);
            if (path.EndsWith('/'))
                Directory.CreateDirectory(full);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, path == ".gitignore" ? "bin/\n*.log\n" : "x");
            }
        }
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    /// <summary>Commits everything in the repo at <paramref name="root"/>.</summary>
    public static void Commit(string root)
    {
        Run(root, "add", "-A");
        Run(root, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "start");
    }

    public static void Run(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var git = Process.Start(start)!;
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
    }
}
