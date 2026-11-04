using System.Globalization;
using System.Text.RegularExpressions;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Remote.Install;

internal sealed record RemoteReleaseReceipt(string Version, string Commit);

/// <summary>
/// Installs the latest signed backend on a saved machine. The desktop verifies the signed record; the remote downloads
/// the archive and the adapters straight from GitHub, checks them, then installs, starts and is read back.
/// </summary>
internal sealed partial class RemoteReleaseInstall(RemoteReleaseFeed feed, RemoteReleaseShell shell, TimeProvider time)
{
    internal async Task<RemoteReleaseReceipt> InstallLatestAsync(SavedMachine machine, Action<string> phase, CancellationToken ct)
    {
        Validate(machine);
        phase("Finding the latest signed backend…");
        var release = await feed.LatestAsync(ct);
        var target = machine.Ssh;

        phase("Checking the remote operating system and download tools…");
        var platform = await shell.RunAsync(target, "printf '%s %s' \"$(uname -s)\" \"$(uname -m)\"", null, ct);
        if (platform != "Linux x86_64") throw new CallFailed($"Unsupported remote platform: {platform}", "unsupported_platform");
        await shell.RunAsync(target, RequireToolsCommand, null, ct);
        var home = await shell.RunAsync(target, "printf '%s' \"$HOME\"", null, ct);
        if (!home.StartsWith('/') || home == "/" || home.Contains('\n') || home.Contains('\r'))
            throw new CallFailed("Remote home directory is invalid.", "remote_invalid");

        var stage = $"{home}/.cache/.assembly-install-{Guid.NewGuid():N}";
        var quoted = Quote(stage);
        try
        {
            await shell.RunAsync(target, $"install -d -m 700 {quoted}", null, ct);
            phase("Downloading backend on the remote machine…");
            var inspected = await ReceiveAsync(target, stage, release, ct);
            phase("Installing and starting the backend…");
            await shell.RunAsync(target, $"set -eu; cd {quoted}; sh install.sh {machine.Port.ToString(CultureInfo.InvariantCulture)} {Quote(machine.TokenFile)}", null, ct);
            phase("Verifying the service, database, and loopback listener…");
            var receipt = await shell.RunAsync(target, ReceiptCommand(machine.Port), null, ct);
            if (receipt != inspected.ServerSha256) throw new CallFailed("Installed backend checksum does not match the signed package.", "install_mismatch");
            return new(inspected.Version, inspected.Commit);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20), time);
            await shell.RunAsync(target, CleanupCommand(stage), null, cleanup.Token);
        }
    }

    private sealed record Inspected(string Version, string Commit, string ServerSha256);

    private async Task<Inspected> ReceiveAsync(string target, string stage, VerifiedRemoteRelease release, CancellationToken ct)
    {
        var record = release.Record;
        var archive = stage + "/package.tar.gz";
        var command = $"set -eu; cd {Quote(stage)}; python3 -u -c {Quote(RemoteReleaseScript.Download)} {Quote(release.ArchiveUrl)} {Quote(archive)} " +
            $"{record.Bytes.ToString(CultureInfo.InvariantCulture)} {Quote(record.Sha256)} {Quote(record.Version)} & child=$!; printf '%s\\n' \"$child\" > download.pid; wait \"$child\"; rm -f download.pid";
        Inspected? inspected = null;
        CallFailed? invalid = null;
        await shell.RunAsync(target, command, line =>
        {
            if (line.StartsWith("P\t", StringComparison.Ordinal))
            {
                if (!long.TryParse(line.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes > record.Bytes)
                    invalid ??= new("Backend download progress exceeds signed size.", "release_invalid");
            }
            else if (line.StartsWith("V\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t');
                if (parts.Length != 4 || parts[1] != record.Version || parts[2].Length < 12 || !RemoteInstallPackage.Hex(parts[3], 64))
                    invalid ??= new("Remote backend verification receipt is invalid.", "release_invalid");
                else inspected = new(parts[1], parts[2], parts[3]);
            }
            else invalid ??= new($"Unexpected remote backend download output: {line}", "release_invalid");
        }, ct);
        if (invalid is not null) throw invalid;
        return inspected ?? throw new CallFailed("Remote backend download finished without verification.", "release_invalid");
    }

    private const string RequireToolsCommand = "set -eu; PATH=$HOME/.local/bin:$PATH; for tool in python3 sha256sum systemctl install ss awk grep bash curl tar node; do command -v \"$tool\" >/dev/null || { echo \"Missing remote install tool: $tool\" >&2; exit 1; }; done; python3 -c 'import ssl, tarfile, urllib.request, sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' || { echo 'Remote install requires Python 3.9 or newer with SSL support' >&2; exit 1; }";

    private static string ReceiptCommand(int port)
    {
        var value = port.ToString(CultureInfo.InvariantCulture);
        return "set -eu; systemctl --user is-active --quiet assembly-dotnet.service; test -f \"$HOME/assembly-dotnet/serve/assembly.db\"; " +
            $"listeners=$(ss -ltnH 'sport = :{value}'); test -n \"$listeners\"; printf '%s\\n' \"$listeners\" | awk '$4 != \"127.0.0.1:{value}\" {{ exit 1 }} END {{ if (NR == 0) exit 1 }}'; " +
            "sha256sum \"$HOME/assembly-dotnet/bin/assembly-remote-server\" | awk '{print $1}'";
    }

    private static string CleanupCommand(string stage) =>
        $"stage={Quote(stage)}; if test -r \"$stage/download.pid\"; then pid=$(cat \"$stage/download.pid\"); case \"$pid\" in ''|*[!0-9]*) ;; *) if test -r \"/proc/$pid/cmdline\" && grep -aFq -- {Quote(stage + "/package.tar.gz")} \"/proc/$pid/cmdline\"; then kill -KILL \"$pid\" 2>/dev/null || true; fi;; esac; fi; find \"$stage\" -depth -delete 2>/dev/null || true";

    private static void Validate(SavedMachine machine)
    {
        if (!SafeTarget().IsMatch(machine.Ssh)) throw new CallFailed("The machine's SSH target is invalid.", "bad_args");
        if (machine.Port is <= 0 or > 65535) throw new CallFailed("The machine's port is invalid.", "bad_args");
        if (!SafeTokenFile().IsMatch(machine.TokenFile) || machine.TokenFile.Split('/').Contains(".."))
            throw new CallFailed("The machine's token file is invalid.", "bad_args");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex(@"^[A-Za-z0-9_.@][A-Za-z0-9_.@:-]*$")]
    private static partial Regex SafeTarget();

    [GeneratedRegex(@"^[A-Za-z0-9_./-]+$")]
    private static partial Regex SafeTokenFile();
}
