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

    /// <summary>Exit success with the old version still there is a failure: <c>version_readback_mismatch</c>.</summary>
    internal static async Task RunAsync(RemoteReleaseShell shell, string target, CliBehind cli, string agentsFile, CancellationToken ct)
    {
        var said = await shell.RunAsync(target, Command(cli, agentsFile), null, ct);
        var version = CliCommand.ReadVersion(cli.Family, said.Split('\n')[^1].Trim());
        if (version is null || CliVersion.Compare(version, cli.Version) == "update_available")
            throw new CallFailed($"{cli.Family} still says {version ?? "something else"} after updating to {cli.Version}.", "version_readback_mismatch");
    }

    internal static string Command(CliBehind cli, string agentsFile)
    {
        if (!CliVersion.IsStable(cli.Version) || !CliCommand.Supports(cli.Family)) throw new CallFailed($"Can't update {cli.Family} to {cli.Version}.", "bad_args");
        var update = CliCommand.UpdatesItself(cli.Family)
            ? "\"$cli\" update >&2"
            : $"command -v npm >/dev/null || {{ echo '{cli.Family} updates through npm, and npm is missing' >&2; exit 1; }}; " +
              $"[ -f \"$(npm root -g)/{CliCommand.PackageOf(cli.Family)}/package.json\" ] || {{ echo '{cli.Family} was not installed with npm: update it the way it was installed' >&2; exit 1; }}; " +
              $"npm install --global --no-audit --no-fund {CliCommand.PackageOf(cli.Family)}@{cli.Version} >&2";
        return $"set -eu; PATH=$HOME/.local/bin:/usr/local/bin:$PATH; cli=$(node -e \"{Configured}\" {agentsFile} {cli.Family}); {update}; \"$cli\" --version";
    }
}

/// <summary>Runs on this computer, through the account's login shell: an app opened from the Finder has almost no PATH.</summary>
internal sealed class ThisMachineShell : RemoteReleaseShell
{
    internal override Task<string> RunAsync(string target, string command, Action<string>? line, CancellationToken ct) =>
        RunProcessAsync(new AgentCommand(Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : "/bin/sh", ["-lc", command], Directory.GetCurrentDirectory()), null, line, ct);
}

/// <summary>
/// This computer's adapters and CLIs in a delivered app. Adapters come from the same signed release through the same installer
/// a remote runs, shipped in the app beside the backend. A new bundle goes beside the one a running session uses.
/// </summary>
internal sealed class LocalReleaseTools(RemoteReleaseFeed feed, RemoteReleaseShell shell, string agentsFile, string adapters, Func<bool> sessionRunning, string? installer = null)
{
    private const string RequireTools = "for tool in bash curl tar node python3 sha256sum; do command -v \"$tool\" >/dev/null || { echo \"Missing tool on this computer: $tool\" >&2; exit 1; }; done; " +
        "python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)' || { echo 'Updating adapters requires Python 3.11 or newer on this computer' >&2; exit 1; }";

    internal async Task UpdateAsync(ToolsBehind tools, Action<string> progress, CancellationToken ct)
    {
        if (tools.Adapters)
        {
            progress("Updating this computer's adapters…");
            var verified = await feed.AdaptersForAsync(Target(), ct);
            var script = installer ?? Path.Combine(AppContext.BaseDirectory, "agents-file.sh");
            await shell.RunAsync("", $"set -eu; {RequireTools}; ASSEMBLY_ADAPTER_TAG={Quote(verified.Tag)} ASSEMBLY_ADAPTER_MANIFEST_SHA256={Quote(verified.ManifestSha256)} bash {Quote(script)} {Quote(adapters)} {Quote(agentsFile)}", null, ct);
        }
        if (tools.Clis.Count == 0) return;
        // Replacing a CLI's files could break a turn it's in the middle of. There's no remote host to wait on here, and pressing the button again is cheap.
        if (sessionRunning()) throw new CallFailed("A session is running here, so this computer's CLIs weren't updated. Update again when it finishes.", "remote_busy");
        foreach (var cli in tools.Clis)
        {
            progress($"Updating {cli.Family} on this computer…");
            await ReleaseCliUpdate.RunAsync(shell, "", cli, Quote(agentsFile), ct);
        }
    }

    // The same names agents-file.sh picks by its uname.
    private static string Target() => (OperatingSystem.IsMacOS() ? "darwin" : "linux") + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-arm64" : "-x64");

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
