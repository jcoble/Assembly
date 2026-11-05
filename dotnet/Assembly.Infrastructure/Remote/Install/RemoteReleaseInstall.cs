using System.Globalization;
using System.Text.RegularExpressions;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Updates;

namespace Assembly.Infrastructure.Remote.Install;

internal sealed record RemoteReleaseReceipt(string Version, string Commit);

/// <summary>
/// Installs the latest signed backend on a saved machine. The desktop verifies the signed record; the remote downloads
/// the archive and the adapters straight from GitHub, checks them, then installs, starts and is read back.
/// </summary>
internal sealed partial class RemoteReleaseInstall(RemoteReleaseFeed feed, RemoteReleaseShell shell, TimeProvider time, string? version = null, IRemotePackages? packages = null)
{
    private readonly IRemotePackages _packages = packages ?? new ReleasePackages(feed, shell, version);

    /// <summary>
    /// Puts the backend on the machine, with its adapters unless <paramref name="keepAdapters"/> says the machine's are
    /// newer, after making sure what's there is an install of this form.
    /// </summary>
    internal async Task<RemoteReleaseReceipt> InstallLatestAsync(SavedMachine machine, Action<string> phase, CancellationToken ct, bool keepAdapters = false)
    {
        ReceivedPackage? inspected = null;
        string? serverSha = null;
        await WithPackageAsync(machine, phase, false, async (target, quoted, package) =>
        {
            inspected = package;
            await WhenIdleAsync(target, quoted, phase, async () =>
            {
                var adapters = await _packages.InstallAdaptersAsync(target, quoted, keepAdapters, phase, ct);
                phase("Installing and starting the backend…");
                await shell.RunAsync(target, $"set -eu; cd {quoted}; sh install.sh {Arguments(machine, adapters)}", null, ct);
            }, ct);
            phase("Verifying the service, database, and loopback listener…");
            serverSha = await shell.RunAsync(target, ReceiptCommand(machine.Port), null, ct);
        }, ct);
        if (serverSha != inspected!.ServerSha256) throw new CallFailed("Installed backend checksum does not match the package.", "install_mismatch");
        return new(inspected.Version, inspected.Commit);
    }

    /// <summary>
    /// Updates what's behind on a machine whose backend is current, and leaves that backend's files alone. A new adapter
    /// needs the running host to restart; a CLI doesn't, since each session starts its own.
    /// </summary>
    internal Task UpdateToolsAsync(SavedMachine machine, ToolsBehind tools, Action<string> phase, CancellationToken ct) =>
        WithPackageAsync(machine, phase, true, (target, quoted, _) => WhenIdleAsync(target, quoted, phase, async () =>
        {
            if (await _packages.UpdateToolsAsync(target, quoted, tools, phase, ct) is not { } adapters) return;
            phase("Installing adapters and restarting the backend…");
            await shell.RunAsync(target, $"set -eu; cd {quoted}; sh install.sh --adapters {Arguments(machine, adapters)}", null, ct);
        }, ct), ct);

    private static string Arguments(SavedMachine machine, AdapterArguments adapters) =>
        $"{(adapters.Keep ? "--keep-adapters " : "")}{machine.Port.ToString(CultureInfo.InvariantCulture)} {Quote(adapters.Tag)} {Quote(adapters.ManifestSha256)}";

    // Everything that needs the signed package on the machine: the installer comes in it. The stage goes with the call.
    private async Task WithPackageAsync(SavedMachine machine, Action<string> phase, bool toolsOnly, Func<string, string, ReceivedPackage, Task> act, CancellationToken ct)
    {
        Validate(machine);
        phase("Finding the signed release…");
        await _packages.PrepareAsync(ct);
        var target = machine.Ssh;

        phase("Checking the remote operating system and download tools…");
        var platform = await shell.RunAsync(target, "printf '%s %s' \"$(uname -s)\" \"$(uname -m)\"", null, ct);
        if (platform != "Linux x86_64") throw new CallFailed($"Unsupported remote platform: {platform}", "unsupported_platform");
        await shell.RunAsync(target, RequireToolsCommand, null, ct);
        var home = await shell.RunAsync(target, "printf '%s' \"$HOME\"", null, ct);
        if (!home.StartsWith('/') || home == "/" || home.Contains('\n') || home.Contains('\r'))
            throw new CallFailed("Remote home directory is invalid.", "remote_invalid");
        phase("Checking what's on the remote machine…");
        RemoteMachineCheck.Require(await shell.RunAsync(target, RemoteMachineCheck.Command(machine.Port), null, ct), machine, toolsOnly);
        if (!toolsOnly) await shell.RunAsync(target, RemoteMachineCheck.Claim, null, ct);

        var stage = $"{home}/.cache/.assembly-install-{Guid.NewGuid():N}";
        var quoted = Quote(stage);
        try
        {
            await shell.RunAsync(target, $"install -d -m 700 {quoted}", null, ct);
            await act(target, quoted, await _packages.ReceiveAsync(target, stage, phase, ct));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
            await shell.RunAsync(target, CleanupCommand(stage), null, cleanup.Token);
        }
    }

    // Waits on the machine for running sessions to end, then acts. Nothing here stops one: install.sh checks again directly
    // before it stops anything and refuses, which comes back here to wait again. A cancelled wait leaves a waiter on the
    // machine that only ever waits, and ends with the sessions or when the stage is cleaned up.
    private async Task WhenIdleAsync(string target, string quotedStage, Action<string> phase, Func<Task> act, CancellationToken ct)
    {
        for (; ; )
        {
            await shell.RunAsync(target, $"set -eu; cd {quotedStage}; sh install.sh --wait-idle", _ => phase(WaitingWords), ct);
            try { await act(); return; }
            catch (CallFailed busy) when (busy.Reason == "remote_busy") { phase(WaitingWords); }
        }
    }

    internal const string WaitingWords = "Waiting for running sessions and open terminals to finish…";

    private const string RequireToolsCommand = "set -eu; PATH=$HOME/.local/bin:$PATH; for tool in python3 sha256sum systemctl install ss awk grep bash curl tar node pidwait; do command -v \"$tool\" >/dev/null || { echo \"Missing remote install tool: $tool\" >&2; exit 1; }; done; python3 -c 'import ssl, tarfile, urllib.request, sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)' || { echo 'Remote install requires Python 3.11 or newer with SSL support' >&2; exit 1; }";

    private static string ReceiptCommand(int port)
    {
        var value = port.ToString(CultureInfo.InvariantCulture);
        return "set -eu; systemctl --user is-active --quiet assembly-host.service; test -f \"$HOME/assembly-host/serve/assembly.db\"; " +
            $"listeners=$(ss -ltnH 'sport = :{value}'); test -n \"$listeners\"; printf '%s\\n' \"$listeners\" | awk '$4 != \"127.0.0.1:{value}\" {{ exit 1 }} END {{ if (NR == 0) exit 1 }}'; " +
            "sha256sum \"$HOME/assembly-host/bin/assembly-remote-server\" | awk '{print $1}'";
    }

    private static string CleanupCommand(string stage) =>
        $"stage={Quote(stage)}; if test -r \"$stage/download.pid\"; then pid=$(cat \"$stage/download.pid\"); case \"$pid\" in ''|*[!0-9]*) ;; *) if test -r \"/proc/$pid/cmdline\" && grep -aFq -- {Quote(stage + "/package.tar.gz")} \"/proc/$pid/cmdline\"; then kill -KILL \"$pid\" 2>/dev/null || true; fi;; esac; fi; find \"$stage\" -depth -delete 2>/dev/null || true";

    private static void Validate(SavedMachine machine)
    {
        if (!SafeTarget().IsMatch(machine.Ssh)) throw new CallFailed("The machine's SSH target is invalid.", "bad_args");
        if (machine.Port is <= 0 or > 65535) throw new CallFailed("The machine's port is invalid.", "bad_args");
        // The new install shares nothing with an old one, whose token file a machine saved earlier may still name. That
        // machine keeps connecting to what it reaches now, and this says what to change before it can have an install.
        if (machine.TokenFile != SavedMachine.InstalledTokenFile)
            throw new CallFailed($"{machine.Name} is saved with the token file {machine.TokenFile}, and an install only uses its own, {SavedMachine.InstalledTokenFile}, so nothing was changed. Clear the token file in Settings and install again.", "token_file_not_ours");
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex(@"^[A-Za-z0-9_.@][A-Za-z0-9_.@:-]*$")]
    internal static partial Regex SafeTarget();
}
