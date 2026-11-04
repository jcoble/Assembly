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
    Task UpdateAsync(Action<string> progress, CancellationToken ct);
}

internal static class UpdateSources
{
    /// <summary>app-dev names its checkout; anything else is a delivered app.</summary>
    internal static IUpdateSource ForHost(HttpClient http, TimeProvider time, string? checkout = null) =>
        (checkout ?? Environment.GetEnvironmentVariable("ASSEMBLY_DEV_CHECKOUT")) is { Length: > 0 } root
            ? new CheckoutUpdateSource(root, new(http, time)) : new ReleaseUpdateSource(new(http, time));

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

/// <summary>Development: this checkout is what's wanted, and the list decides whether a remote's backend is behind.</summary>
internal sealed class CheckoutUpdateSource(string root, CliReleaseSource npm) : IUpdateSource
{
    public async Task<UpdateWanted> WantedAsync(CancellationToken ct) => new(BackendBuild.Version, backend => backend.ListDiffers, BackendBuild.Version,
        await UpdateSources.LatestAsync(UpdateSources.AdapterNames, UpdateSources.AdapterPackage, npm, ct), BackendBuild.AdapterSources,
        await UpdateSources.CliVersionsAsync(npm, ct));

    public async Task UpdateAsync(Action<string> progress, CancellationToken ct)
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

/// <summary>
/// A delivered app: the backend must carry this app's own version, and CLIs have npm. The release feed plugs in here once
/// signed releases are published: the newest app version, the adapters in the newest provider release, and an update that
/// has a remote fetch its backend and adapters from GitHub itself.
/// </summary>
internal sealed class ReleaseUpdateSource(CliReleaseSource npm) : IUpdateSource
{
    public async Task<UpdateWanted> WantedAsync(CancellationToken ct) => new(null, backend => backend.VersionDiffers, BackendBuild.Version,
        UpdateSources.AdapterNames.ToDictionary(name => name, _ => (string?)null, StringComparer.Ordinal), null,
        await UpdateSources.CliVersionsAsync(npm, ct));

    public Task UpdateAsync(Action<string> progress, CancellationToken ct) =>
        throw new CallFailed("No signed release is published yet, so there's nothing to update from.", "no_release");
}
