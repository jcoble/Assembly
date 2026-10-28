using System.Diagnostics;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Worktrees;

namespace Assembly.Infrastructure.Worktrees;

/// <summary>
/// git in a folder, with no prompt for credentials and no optional locks, so a read never blocks on or takes the index
/// lock an agent working there needs. A cancel kills it.
/// </summary>
public sealed class GitCli : IGit
{
    public async Task<string> RunAsync(string folder, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(folder);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var git = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        git.StandardInput.Close();
        var stderr = git.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = git.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            await git.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            git.Kill(entireProcessTree: true);
            await git.WaitForExitAsync(CancellationToken.None);
            await stdout;
            throw;
        }

        var output = await stdout;
        var error = await stderr;
        if (git.ExitCode != 0) throw new CallFailed(error.Trim());
        return output;
    }
}
