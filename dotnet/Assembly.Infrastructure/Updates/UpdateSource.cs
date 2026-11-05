using System.Diagnostics;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Updates;

/// <summary>Where wanted versions come from and how an update is carried out: the only thing development and production do differently.</summary>
internal interface IUpdateSource
{
    Task<UpdateWanted> WantedAsync(CancellationToken ct);
    /// <summary>True when a remote on another version is this app's to update. app-dev starts that itself, before the desktop is built.</summary>
    bool StartsRemoteUpdates { get; }
    /// <summary>
    /// Updates everything behind, reporting what it's doing. <paramref name="automatic"/> is when no one pressed the button.
    /// Throws <see cref="CallFailed"/> with the reason when it can't.
    /// </summary>
    Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct);
}

/// <summary>What the last check found behind on one machine; this one when <paramref name="Name"/> is null.</summary>
internal sealed record MachineBehind(string? Name, IReadOnlyList<UpdateItem> Behind);

internal sealed record CliBehind(string Family, string Version);

/// <summary>The adapters and CLIs behind on one machine.</summary>
internal sealed record ToolsBehind(bool Adapters, IReadOnlyList<CliBehind> Clis)
{
    internal bool Any => Adapters || Clis.Count > 0;

    internal static ToolsBehind Of(IEnumerable<UpdateItem> behind)
    {
        var items = behind.ToArray();
        return new(items.Any(item => item.Kind == "adapter"),
            [.. items.Where(item => item.Kind == "cli" && item.Wanted is not null).Select(item => new CliBehind(item.Name, item.Wanted!))]);
    }
}

internal static class UpdateSources
{
    /// <summary>app-dev names its checkout; anything else is a delivered app.</summary>
    internal static IUpdateSource ForHost(HttpClient http, TimeProvider time, IReleaseInstall? release = null, string? checkout = null) =>
        (checkout ?? Environment.GetEnvironmentVariable("ASSEMBLY_DEV_CHECKOUT")) is { Length: > 0 } root
            ? new CheckoutUpdateSource(root, new(http, time)) : new ReleaseUpdateSource(new(http, time), release, time);

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

    public bool StartsRemoteUpdates => false;

    public async Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
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

/// <summary>The signed release: what it says is wanted, and each way of installing from it.</summary>
internal interface IReleaseInstall
{
    Task<PublishedVersions> PublishedAsync(CancellationToken ct);
    /// <summary>The backend and its adapters, waiting for running sessions.</summary>
    Task InstallAsync(string machine, Action<string> progress, CancellationToken ct);
    /// <summary>Adapters and CLIs on a machine, this one when <paramref name="machine"/> is null, leaving a current backend in place.</summary>
    Task UpdateToolsAsync(string? machine, ToolsBehind tools, Action<string> progress, CancellationToken ct);
}

/// <summary>
/// A delivered app: everything is the same except that wanted versions and the update come from the signed release. A build
/// with no <see cref="IReleaseInstall"/> has nothing to update from.
/// </summary>
internal sealed class ReleaseUpdateSource(CliReleaseSource npm, IReleaseInstall? release, TimeProvider time) : IUpdateSource
{
    // A check runs at start and at each remote's hello, and GitHub allows an address 60 unsigned reads an hour.
    private static readonly TimeSpan PublishedLasts = TimeSpan.FromMinutes(10);
    private (PublishedVersions Read, DateTimeOffset At)? _published;

    public bool StartsRemoteUpdates => true;

    public async Task<UpdateWanted> WantedAsync(CancellationToken ct)
    {
        var published = await PublishedAsync(ct);
        return new(published.App, BackendBuild.Version,
            UpdateSources.AdapterNames.ToDictionary(name => name, name => published.Adapters?.GetValueOrDefault(name), StringComparer.Ordinal),
            published.Packaging, await UpdateSources.CliVersionsAsync(npm, ct), NeverOlder: true);
    }

    private async Task<PublishedVersions> PublishedAsync(CancellationToken ct)
    {
        if (release is null) return new(null, null, null);
        if (_published is { } kept && time.GetUtcNow() - kept.At < PublishedLasts) return kept.Read;
        var read = await release.PublishedAsync(ct);
        if (read.App is not null || read.Adapters is not null) _published = (read, time.GetUtcNow());
        return read;
    }

    public async Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
    {
        if (release is null) throw new CallFailed("No signed release is published yet, so there's nothing to update from.", "no_release");
        var failures = new List<string>();
        // One machine that can't be reached doesn't hold up the rest; the card says what didn't happen.
        async Task Try(Func<Task> step)
        {
            try { await step(); }
            catch (CallFailed failed) { failures.Add(failed.Message); }
        }
        foreach (var machine in machines)
        {
            void Note(string note) => progress(machine.Name is null ? note : $"{machine.Name}: {note}");
            var backend = machine.Name is not null && machine.Behind.Any(item => item.Kind == "backend");
            if (backend) await Try(() => release.InstallAsync(machine.Name!, Note, ct));
            // The backend's install brings its adapters; a CLI is separate either way.
            var tools = ToolsBehind.Of(machine.Behind);
            if (backend) tools = tools with { Adapters = false };
            if (tools.Any) await Try(() => release.UpdateToolsAsync(machine.Name, tools, Note, ct));
        }
        // Installing the app itself isn't built yet (Step 19.09). The button says so rather than appearing to have done it.
        var app = machines.FirstOrDefault(machine => machine.Name is null)?.Behind.FirstOrDefault(item => item.Kind == "desktop");
        if (app is not null && !automatic) failures.Add($"Assembly {app.Wanted} is out, and this app can't install it yet: install it from the release page.");
        if (failures.Count > 0) throw new CallFailed(string.Join(" ", failures), "update_failed");
    }
}
