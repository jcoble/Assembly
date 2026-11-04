using System.Diagnostics;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Updates;

/// <summary>Where wanted versions come from and how an update is carried out: the only thing development and production do differently.</summary>
internal interface IUpdateSource
{
    Task<UpdateWanted> WantedAsync(CancellationToken ct);
    /// <summary>Updates everything behind, reporting what it's doing. Throws <see cref="CallFailed"/> with the reason when it can't.</summary>
    Task UpdateAsync(IReadOnlyList<string> remotes, Action<string> progress, CancellationToken ct);
}

internal static class UpdateSources
{
    /// <summary>app-dev names its checkout; anything else is a delivered app.</summary>
    internal static IUpdateSource ForHost(HttpClient http, TimeProvider time, IReleaseInstall? release = null, string? checkout = null) =>
        (checkout ?? Environment.GetEnvironmentVariable("ASSEMBLY_DEV_CHECKOUT")) is { Length: > 0 } root
            ? new CheckoutUpdateSource(root, new(http, time)) : new ReleaseUpdateSource(new(http, time), release);

    internal static async Task<IReadOnlyDictionary<string, string?>> LatestAsync(IEnumerable<string> names, Func<string, string> package, CliReleaseSource npm, CancellationToken ct)
    {
        var found = await Task.WhenAll(names.Select(async name =>
        {
            try { return (name, version: (string?)await npm.PackageAsync(package(name), ct)); }
            catch (CallFailed) { return (name, version: (string?)null); }
        }));
        return found.ToDictionary(item => item.name, item => item.version, StringComparer.Ordinal);
    }

    internal static readonly string[] AdapterNames = ["claude-agent-acp", "codex-acp"];
    internal static string AdapterPackage(string name) => "@agentclientprotocol/" + name;
    internal static Task<IReadOnlyDictionary<string, string?>> CliVersionsAsync(CliReleaseSource npm, CancellationToken ct) =>
        LatestAsync(CliReleaseSource.Families, CliReleaseSource.PackageOf, npm, ct);
}

/// <summary>Development: the update builds this checkout. app-dev starts it on launch when a remote's number differs, without waiting on the desktop.</summary>
internal sealed class CheckoutUpdateSource(string root, CliReleaseSource npm) : IUpdateSource
{
    public async Task<UpdateWanted> WantedAsync(CancellationToken ct) => new(BackendBuild.Version, BackendBuild.Version,
        await UpdateSources.LatestAsync(UpdateSources.AdapterNames, UpdateSources.AdapterPackage, npm, ct), BackendBuild.AdapterSources,
        await UpdateSources.CliVersionsAsync(npm, ct));

    public async Task UpdateAsync(IReadOnlyList<string> remotes, Action<string> progress, CancellationToken ct)
    {
        var start = new ProcessStartInfo("node", [Path.Combine(root, "scripts/update-machines.ts")])
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = new Process { StartInfo = start };
        string? last = null;
        void Line(string? line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("[dev-sync timing]", StringComparison.Ordinal)) return;
            if (!line.StartsWith(Marker, StringComparison.Ordinal)) { last = line; return; }
            using var json = JsonDocument.Parse(line[Marker.Length..]);
            if (json.RootElement.GetProperty("reason").GetString() is { } reason) progress(reason);
        }
        process.OutputDataReceived += (_, line) => Line(line.Data);
        process.ErrorDataReceived += (_, line) => Line(line.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // SIGTERM lets the script stop its own children; a running session on the remote is never touched by a stop.
        await using var stop = ct.Register(() => { try { if (!process.HasExited) _ = Kill(process.Id, 15); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new CallFailed(last ?? $"The update exited {process.ExitCode}.", "update_failed");
    }

    private const string Marker = "ASSEMBLY_REMOTE_SYNC ";

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);
}

/// <summary>The production install, which has each remote fetch its signed backend and adapters from GitHub itself.</summary>
internal interface IReleaseInstall
{
    Task InstallAsync(string machine, Action<string> progress, CancellationToken ct);
}

/// <summary>
/// A delivered app: everything is the same except that the update comes from the signed release. A build with no <see cref="IReleaseInstall"/> has nothing
/// to update from. The app's own feed and the published adapter versions plug in here.
/// </summary>
internal sealed class ReleaseUpdateSource(CliReleaseSource npm, IReleaseInstall? release) : IUpdateSource
{
    public async Task<UpdateWanted> WantedAsync(CancellationToken ct) => new(null, BackendBuild.Version,
        UpdateSources.AdapterNames.ToDictionary(name => name, _ => (string?)null, StringComparer.Ordinal), null, await UpdateSources.CliVersionsAsync(npm, ct));

    public async Task UpdateAsync(IReadOnlyList<string> remotes, Action<string> progress, CancellationToken ct)
    {
        if (release is null) throw new CallFailed("No signed release is published yet, so there's nothing to update from.", "no_release");
        foreach (var machine in remotes) await release.InstallAsync(machine, progress, ct);
    }
}
