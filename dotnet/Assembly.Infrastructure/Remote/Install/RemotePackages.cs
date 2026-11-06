using System.Diagnostics;
using System.Globalization;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Updates;

namespace Assembly.Infrastructure.Remote.Install;

/// <summary>What's unpacked in the stage, read from its own manifest.</summary>
internal sealed record ReceivedPackage(string Version, string Commit, string ServerSha256);

/// <summary>The rest of install.sh's arguments for adapters: the two values, and whether it should leave them alone.</summary>
internal sealed record AdapterArguments(bool Keep, string Tag, string ManifestSha256);

/// <summary>Where an install's files come from: the only thing development and a delivered app do differently.</summary>
internal interface IRemotePackages
{
    /// <summary>Answers before anything touches the machine: a version with no release stops here. Each install has its own answer.</summary>
    Task<IRemotePackage> PrepareAsync(CancellationToken ct);
}

/// <summary>The package one install uses, from start to finish. Machines install side by side.</summary>
internal interface IRemotePackage
{
    /// <summary>Gets the package onto the machine and unpacks it in the stage.</summary>
    Task<ReceivedPackage> ReceiveAsync(string target, string stage, Action<string> phase, CancellationToken ct);
    /// <summary>The adapters for a backend install.</summary>
    Task<AdapterArguments> InstallAdaptersAsync(string target, string quotedStage, bool keep, Action<string> phase, CancellationToken ct);
    /// <summary>What's behind beside the backend. Null means the running host doesn't need a restart.</summary>
    Task<AdapterArguments?> UpdateToolsAsync(string target, string quotedStage, ToolsBehind tools, Action<string> phase, CancellationToken ct);
}

/// <summary>
/// Development: this checkout, built here for Linux and sent over SSH. Adapters and CLIs are installed with the same
/// script a lane's are, from the scripts in the package, so they match this checkout's sources.
/// </summary>
internal sealed class CheckoutPackages(RemoteReleaseShell shell, Func<CancellationToken, Task<string>> build) : IRemotePackages, IRemotePackage
{
    internal const string Adapters = "\"$HOME/assembly-host/adapters\"", Agents = "\"$HOME/assembly-host/serve/agents.json\"";

    public Task<IRemotePackage> PrepareAsync(CancellationToken ct) => Task.FromResult<IRemotePackage>(this);

    public async Task<ReceivedPackage> ReceiveAsync(string target, string stage, Action<string> phase, CancellationToken ct)
    {
        phase("Building this checkout for the remote machine…");
        var archive = await build(ct);
        phase("Sending the backend to the remote machine…");
        await shell.UploadAsync(target, archive, stage + "/package.tar.gz", ct);
        await using var read = File.OpenRead(archive);
        var sha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(read, ct));
        var said = await shell.RunAsync(target, $"set -eu; cd {RemoteReleaseInstall.Quote(stage)}; " +
            $"test \"$(sha256sum package.tar.gz | cut -d' ' -f1)\" = {sha}; tar -xzf package.tar.gz; sha256sum -c SHA256SUMS >/dev/null; " +
            "python3 -I -c 'import hashlib,json; m=json.load(open(\"manifest.json\")); print(m[\"version\"], m[\"commit\"], hashlib.sha256(open(\"payload/assembly-remote-server\",\"rb\").read()).hexdigest())'", null, ct);
        var parts = said.Split(' ');
        return parts.Length == 3 ? new(parts[0], parts[1], parts[2]) : throw new CallFailed("The sent package has no readable manifest.", "release_invalid");
    }

    public async Task<AdapterArguments> InstallAdaptersAsync(string target, string quotedStage, bool keep, Action<string> phase, CancellationToken ct)
    {
        await ToolsAsync(target, quotedStage, phase, ct);
        return new(true, "", "");
    }

    public async Task<AdapterArguments?> UpdateToolsAsync(string target, string quotedStage, ToolsBehind tools, Action<string> phase, CancellationToken ct)
    {
        await ToolsAsync(target, quotedStage, phase, ct);
        return new(true, "", "");
    }

    private Task ToolsAsync(string target, string quotedStage, Action<string> phase, CancellationToken ct)
    {
        phase("Installing adapters and CLIs on the remote machine…");
        return shell.RunAsync(target, $"set -eu; PATH=$HOME/.local/bin:/usr/local/bin:/usr/bin:/bin; mkdir -p \"$HOME/assembly-host/serve\"; cd {quotedStage}; node payload/scripts/dev-remote-tools.ts {Adapters} {Agents} >&2", null, ct);
    }
}

/// <summary>Builds this checkout's package with its own script, on this machine, once per build inputs.</summary>
internal static class CheckoutBuild
{
    internal static Func<CancellationToken, Task<string>> For(string root, string home)
    {
        // Machines update side by side, and the build's folder is one.
        var one = new SemaphoreSlim(1, 1);
        return async ct =>
        {
            await one.WaitAsync(ct);
            try { return await BuildAsync(root, home, ct); }
            finally { one.Release(); }
        };
    }

    private static async Task<string> BuildAsync(string root, string home, CancellationToken ct)
    {
        var start = new ProcessStartInfo("node", [Path.Combine(root, "scripts/package-checkout.ts"), Path.Combine(home, "staging", "checkout-package")])
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new CallFailed("The checkout's build could not start.", "build_failed");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        if (process.ExitCode != 0) throw new CallFailed("This checkout didn't build: " + string.Join(' ', (await errors).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)), "build_failed");
        return (await output).Trim().Split('\n')[^1];
    }
}

/// <summary>A delivered app: the signed release. The remote downloads it itself, and the desktop checks what it says it got.</summary>
internal sealed class ReleasePackages(RemoteReleaseFeed feed, RemoteReleaseShell shell, string? version, UpdateProgress? updateProgress = null) : IRemotePackages
{
    public async Task<IRemotePackage> PrepareAsync(CancellationToken ct) =>
        new ReleasePackage(await (version is null ? feed.LatestAsync(ct) : feed.ForVersionAsync(version, ct)), shell, updateProgress);
}

/// <summary>One signed release, for one install.</summary>
internal sealed class ReleasePackage(VerifiedRemoteRelease release, RemoteReleaseShell shell, UpdateProgress? updateProgress = null) : IRemotePackage
{
    public async Task<ReceivedPackage> ReceiveAsync(string target, string stage, Action<string> phase, CancellationToken ct)
    {
        phase("Downloading backend on the remote machine…");
        var record = release.Record;
        var archive = stage + "/package.tar.gz";
        var command = $"set -eu; cd {RemoteReleaseInstall.Quote(stage)}; python3 -u -c {RemoteReleaseInstall.Quote(RemoteReleaseScript.Download)} {RemoteReleaseInstall.Quote(release.ArchiveUrl)} {RemoteReleaseInstall.Quote(archive)} " +
            $"{record.Bytes.ToString(CultureInfo.InvariantCulture)} {RemoteReleaseInstall.Quote(record.Sha256)} {RemoteReleaseInstall.Quote(record.Version)} & child=$!; printf '%s\\n' \"$child\" > download.pid; wait \"$child\"; rm -f download.pid";
        ReceivedPackage? inspected = null;
        CallFailed? invalid = null;
        await shell.RunAsync(target, command, line =>
        {
            if (line.StartsWith("P\t", StringComparison.Ordinal))
            {
                if (!long.TryParse(line.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes > record.Bytes)
                    invalid ??= new("Backend download progress exceeds signed size.", "release_invalid");
                else updateProgress?.Download(target, "backend", bytes, record.Bytes);
            }
            else if (line.StartsWith("V\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t');
                if (parts.Length != 4 || parts[1] != record.Version || parts[2].Length < 12 || !RemoteReleaseFeed.Hex(parts[3], 64))
                    invalid ??= new("Remote backend verification receipt is invalid.", "release_invalid");
                else inspected = new(parts[1], parts[2], parts[3]);
            }
            else invalid ??= new($"Unexpected remote backend download output: {line}", "release_invalid");
        }, ct);
        if (invalid is not null) throw invalid;
        return inspected ?? throw new CallFailed("Remote backend download finished without verification.", "release_invalid");
    }

    public Task<AdapterArguments> InstallAdaptersAsync(string target, string quotedStage, bool keep, Action<string> phase, CancellationToken ct) =>
        Task.FromResult(new AdapterArguments(keep, release.Adapters.Tag, release.Adapters.ManifestSha256));

    public async Task<AdapterArguments?> UpdateToolsAsync(string target, string quotedStage, ToolsBehind tools, Action<string> phase, CancellationToken ct)
    {
        foreach (var cli in tools.Clis)
        {
            phase($"Updating {cli.Family}…");
            await ReleaseCliUpdate.RunAsync(shell, target, cli, CheckoutPackages.Agents, ct);
            updateProgress?.Piece(target);
        }
        if (!tools.Adapters) return null;
        return new(false, release.Adapters.Tag, release.Adapters.ManifestSha256);
    }
}
