using System.Runtime.InteropServices;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;

namespace Assembly.Infrastructure.Remote.Install;

/// <summary>Updates one CLI the way it was installed, on whichever machine the shell reaches, and reads the version back.</summary>
internal static class ReleaseCliUpdate
{
    // Names the CLI the agents file really runs, which may not be the first one on PATH.
    private const string Configured = "const a=JSON.parse(require('fs').readFileSync(process.argv[1],'utf8')).find(x=>x.vendorFamily===process.argv[2]);console.log(a&&a.cli||process.argv[2])";
    // The file that CLI really is, through every symlink: which install owns it decides how it updates.
    private const string Resolved = "console.log(require('fs').realpathSync(process.argv[1]))";

    /// <summary>
    /// Exit success with the old version still there is a failure: <c>version_readback_mismatch</c>. The version is read first:
    /// the check that said it's behind may be old by now, and a CLI newer than wanted stays.
    /// </summary>
    internal static async Task RunAsync(RemoteReleaseShell shell, string target, CliBehind cli, string agentsFile, CancellationToken ct)
    {
        try { await UpdateAsync(shell, target, cli, agentsFile, ct); }
        catch (CallFailed failed) when (!failed.Message.Contains(cli.Family, StringComparison.Ordinal))
        { throw new CallFailed($"{cli.Family}: {failed.Message}", failed.Reason); }
    }

    private static async Task UpdateAsync(RemoteReleaseShell shell, string target, CliBehind cli, string agentsFile, CancellationToken ct)
    {
        var now = await shell.RunAsync(target, $"set -eu; PATH=$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:$PATH; cli=$(node -e \"{Configured}\" {agentsFile} {cli.Family}); \"$cli\" --version || true", null, ct);
        if (CliCommand.ReadVersion(cli.Family, now.Split('\n')[^1].Trim()) is { } found && CliVersion.Compare(found, cli.Version) != "update_available") return;
        var said = await shell.RunAsync(target, Command(cli, agentsFile), null, ct);
        var version = CliCommand.ReadVersion(cli.Family, said.Split('\n')[^1].Trim());
        if (version is null || CliVersion.Compare(version, cli.Version) == "update_available")
            throw new CallFailed($"{cli.Family} still says {version ?? "something else"} after updating to {cli.Version}.", "version_readback_mismatch");
    }

    internal static string Command(CliBehind cli, string agentsFile)
    {
        if (!CliVersion.IsStable(cli.Version) || !CliCommand.Supports(cli.Family)) throw new CallFailed($"Can't update {cli.Family} to {cli.Version}.", "bad_args");
        var update = CliCommand.UpdateMethodOf(cli.Family) switch
        {
            // Its own update first; when that fails, the vendor's official installer.
            CliUpdateMethod.SelfUpdate => $"\"$cli\" update >&2 || {{ {CliCommand.OfficialInstallerOf(cli.Family)} >&2; }}",
            CliUpdateMethod.InstalledMethod => InstalledMethodUpdate(cli),
            _ => throw new CallFailed($"Can't update {cli.Family} to {cli.Version}.", "bad_args")
        };
        return $"set -eu; PATH=$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:$PATH; cli=$(node -e \"{Configured}\" {agentsFile} {cli.Family}); {update}; \"$cli\" --version";
    }

    private static string InstalledMethodUpdate(CliBehind cli)
    {
        var package = CliCommand.PackageOf(cli.Family);
        var brew = CliCommand.HomebrewNameOf(cli.Family);
        // The install that owns the configured executable decides, decided the way the CLI probe
        // decides npm ownership: resolved through its symlinks, an npm copy lives under the global
        // root's package and a Homebrew copy under its Cellar or Caskroom. Any other copy updates
        // itself, and when that fails the vendor's official installer is the fallback.
        // Prefixes go through the same resolution, so a symlinked prefix still matches its own files.
        var decide = $"resolved=$(node -e \"{Resolved}\" \"$cli\" 2>/dev/null || printf %s \"$cli\"); " +
                     $"npmroot=$(npm root -g 2>/dev/null || true); npmroot=$(node -e \"{Resolved}\" \"$npmroot\" 2>/dev/null || printf %s \"$npmroot\"); " +
                     $"brewpre=$(brew --prefix 2>/dev/null || true); brewpre=$(node -e \"{Resolved}\" \"$brewpre\" 2>/dev/null || printf %s \"$brewpre\"); ";
        var brewArms = brew is null
            ? ""
            : $"\"$brewpre/Cellar/{brew}/\"*) brew upgrade {brew} >&2 ;; " +
              $"\"$brewpre/Caskroom/{brew}/\"*) brew upgrade --cask {brew} >&2 ;; ";
        return decide +
               "case \"$resolved\" in " +
               $"\"$npmroot/{package}/\"*) npm install --global --no-audit --no-fund {package}@{cli.Version} >&2 ;; " +
               brewArms +
               $"*) \"$cli\" update >&2 || {{ {CliCommand.OfficialInstallerOf(cli.Family)} >&2; }} ;; esac";
    }
}

/// <summary>
/// Runs on this computer, through the account's login shell: an app opened from the Finder has almost no PATH. The commands
/// are POSIX and the login shell may not be, so it only hands them to sh, in a form every shell reads the same.
/// </summary>
internal sealed class ThisMachineShell(string? loginShell = null) : RemoteReleaseShell
{
    private const string Command = "ASSEMBLY_POSIX_COMMAND";

    internal override Task<string> RunAsync(string target, string command, Action<string>? line, CancellationToken ct) =>
        RunProcessAsync(new AgentCommand(loginShell ?? (Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : "/bin/sh"),
            ["-lc", $"exec /bin/sh -c \"${Command}\""], Directory.GetCurrentDirectory()), new Dictionary<string, string> { [Command] = command }, line, ct);
}

/// <summary>
/// This computer's adapters and CLIs in a delivered app. Adapters come from the same signed release through the same installer
/// a remote runs, shipped in the app beside the backend. A new bundle goes beside the one a running session uses.
/// </summary>
internal sealed class LocalReleaseTools(RemoteReleaseFeed feed, RemoteReleaseShell shell, string agentsFile, string adapters, Func<bool> sessionRunning, Func<CancellationToken, Task> whenIdle, string? installer = null, UpdateProgress? updateProgress = null)
{
    private const string RequireTools = "for tool in bash curl tar node python3 sha256sum; do command -v \"$tool\" >/dev/null || { echo \"Missing tool on this computer: $tool\" >&2; exit 1; }; done; " +
        "python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)' || { echo 'Updating adapters requires Python 3.11 or newer on this computer' >&2; exit 1; }";

    internal async Task UpdateAsync(ToolsBehind tools, Action<string> progress, CancellationToken ct)
    {
        updateProgress?.Installing(UpdateProgress.ThisMachine);
        if (tools.Adapters)
        {
            progress("Updating this computer's adapters…");
            var script = installer ?? Path.Combine(AppContext.BaseDirectory, "agents-file.sh");
            try
            {
                var verified = await feed.AdaptersForAsync(Target(), ct);
                await shell.RunAsync("", $"set -eu; {RequireTools}; ASSEMBLY_ADAPTER_TAG={Quote(verified.Tag)} ASSEMBLY_ADAPTER_MANIFEST_SHA256={Quote(verified.ManifestSha256)} bash {Quote(script)} {Quote(adapters)} {Quote(agentsFile)}", null, ct);
            }
            catch (CallFailed failed) { throw new CallFailed($"Adapters: {failed.Message}", failed.Reason); }
            updateProgress?.Piece(UpdateProgress.ThisMachine);
        }
        if (tools.Clis.Count == 0) return;
        // Replacing a CLI's files could break a turn it's in the middle of, so it waits for this computer's runs to end, then goes on.
        if (sessionRunning())
        {
            progress(AppUpdate.WaitingWords);
            await whenIdle(ct);
        }
        foreach (var cli in tools.Clis)
        {
            progress($"Updating {cli.Family} on this computer…");
            await ReleaseCliUpdate.RunAsync(shell, "", cli, Quote(agentsFile), ct);
            updateProgress?.Piece(UpdateProgress.ThisMachine);
        }
    }

    // The same names agents-file.sh picks by its uname.
    private static string Target() => (OperatingSystem.IsMacOS() ? "darwin" : "linux") + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-arm64" : "-x64");

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
