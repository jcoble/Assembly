using System.Diagnostics;

using Assembly.Application.Common.Agents;
namespace Assembly.Infrastructure.Agents;

/// <summary>A session's own clone of its repo, made on its first send. A clone that fails or is stopped leaves nothing behind.</summary>
public static class GitCheckout
{
    public static async Task EnsureAsync(string repoUrl, string folder, CancellationToken cancellationToken)
    {
        if (Directory.Exists(Path.Combine(folder, ".git")))
            return;

        var partial = folder + ".partial";
        if (Directory.Exists(partial))
            Directory.Delete(partial, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);

        var start = new ProcessStartInfo("git") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in new[] { "clone", "--quiet", "--", repoUrl, partial })
            start.ArgumentList.Add(argument);
        // A private repo with no credentials fails instead of waiting for a password nobody will type.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var git = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var stderr = git.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = git.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            await git.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            git.Kill(entireProcessTree: true);
            await git.WaitForExitAsync(CancellationToken.None);
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
            throw;
        }

        if (git.ExitCode != 0)
        {
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
            throw new InvalidOperationException($"git clone {repoUrl} failed: {(await stderr).Trim()}");
        }

        Directory.Move(partial, folder);
    }
}
