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
    /// <param name="ct">Stops the reads.</param>
    /// <param name="fresh">Someone asked, so nothing kept from an earlier read answers for the release.</param>
    Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false);
    /// <summary>True when a remote on an older version is this app's to update. app-dev starts its own lane's itself, before the desktop is built.</summary>
    bool StartsRemoteUpdates(string machine);
    /// <summary>A lane's own remote is updated by app-dev's script with the lane's token file, so what it's saved with doesn't stop it.</summary>
    bool IgnoresTokenFile(string machine) => false;
    /// <summary>True when an app behind installs this version by itself. One that failed to start is left to the button.</summary>
    bool StartsAppUpdate(string version) => false;
    /// <summary>What the last app update left to say.</summary>
    string? AppNote => null;
    /// <summary>The version of this app waiting for its restart.</summary>
    string? AppReady => null;
    /// <summary>Someone pressed Restart to Update. A ready update also installs on the next quit, without it.</summary>
    Task RestartAppAsync(Action<string> progress, CancellationToken ct) => throw new CallFailed("No update is ready to restart into.", "no_update");
    /// <summary>This app's window is up.</summary>
    void AppStarted() { }
    /// <summary>Puts a backend on a saved machine, from wherever this source's files come from, and connects.</summary>
    Task InstallAsync(string machine, Action<string> progress, CancellationToken ct);
    /// <summary>
    /// Updates everything behind, reporting what it's doing. <paramref name="automatic"/> is when no one pressed the button.
    /// Throws <see cref="CallFailed"/> with the reason when it can't.
    /// </summary>
    Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct);
}

/// <summary>
/// What the last check found behind on one machine; this one when <paramref name="Name"/> is null.
/// <paramref name="AdaptersNewer"/> is when its adapters are ahead of what a release brings, so a backend install leaves them.
/// </summary>
internal sealed record MachineBehind(string? Name, IReadOnlyList<UpdateItem> Behind, bool AdaptersNewer = false);

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
    internal static string? Checkout => Environment.GetEnvironmentVariable("ASSEMBLY_DEV_CHECKOUT") is { Length: > 0 } root ? root : null;

    internal static IUpdateSource ForHost(HttpClient http, TimeProvider time, IReleaseInstall? release = null, string? checkout = null, string? home = null, IAppInstall? app = null) =>
        (checkout ?? Checkout) is { Length: > 0 } root
            ? new CheckoutUpdateSource(root, new(http, time), release, home) : new ReleaseUpdateSource(new(http, time), release, time, app);

    /// <summary>
    /// Each remote's share of the one update, the same from a checkout or a release. One that can't be reached doesn't hold
    /// up the rest; the failures come back together.
    /// </summary>
    internal static async Task RemotesAsync(IReleaseInstall release, IEnumerable<MachineBehind> machines, Action<string> progress, List<string> failures, CancellationToken ct)
    {
        foreach (var machine in machines)
        {
            void Note(string note) => progress(machine.Name is null ? note : $"{machine.Name}: {note}");
            async Task Try(string? item, Func<Task> step)
            {
                try { await step(); }
                catch (CallFailed failed) { failures.Add(item is null ? failed.Message : $"{item}: {failed.Message}"); }
            }
            var backend = machine.Name is not null && machine.Behind.Any(item => item.Kind == "backend");
            if (backend) await Try("Backend", () => release.InstallAsync(machine.Name!, Note, ct, machine.AdaptersNewer));
            // The backend's install brings its adapters; a CLI is separate either way.
            var tools = ToolsBehind.Of(machine.Behind);
            if (backend) tools = tools with { Adapters = false };
            if (tools.Any) await Try(null, () => release.UpdateToolsAsync(machine.Name, tools, Note, ct));
        }
    }

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

/// <summary>
/// Development: this checkout's files, and the machines in this app's Settings and no others. This computer's tools are
/// updated in this app's own home. A lane's own remote is the one app-dev keeps current: it starts that on launch when the
/// number is lower, without waiting on the desktop, and the button runs the same thing for it.
/// </summary>
internal sealed class CheckoutUpdateSource(string root, CliReleaseSource npm, IReleaseInstall? release = null, string? home = null) : IUpdateSource
{
    public async Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false) => new(BackendBuild.Version, BackendBuild.Version,
        await UpdateSources.LatestAsync(UpdateSources.AdapterNames, UpdateSources.AdapterPackage, npm, ct), BackendBuild.AdapterSources,
        await UpdateSources.CliVersionsAsync(npm, ct));

    public bool StartsRemoteUpdates(string machine) => !Lane(machine);
    public bool IgnoresTokenFile(string machine) => Lane(machine);

    private static bool Lane(string? machine) => machine == DevRemoteSyncStatus.Machine && Environment.GetEnvironmentVariable(DevRemoteSyncStatus.Variable) is { Length: > 0 };

    public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) =>
        (release ?? throw new CallFailed("This app can't install on a remote machine.", "no_release")).InstallAsync(machine, progress, ct);

    public async Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
    {
        var failures = new List<string>();
        var here = machines.FirstOrDefault(machine => machine.Name is null);
        if (home is not null && here is not null && ToolsBehind.Of(here.Behind).Any)
            await Script(["--this-machine", "--home", home], progress, failures, ct);
        if (machines.Any(machine => Lane(machine.Name) && machine.Behind.Count > 0))
            await Script(["--lane-remote"], progress, failures, ct);
        var others = machines.Where(machine => machine.Name is not null && !Lane(machine.Name) && machine.Behind.Count > 0).ToArray();
        if (others.Length > 0)
            await UpdateSources.RemotesAsync(release ?? throw new CallFailed("This app can't update a remote machine.", "no_release"), others, progress, failures, ct);
        if (failures.Count > 0) throw new CallFailed(string.Join(" ", failures), "update_failed");
    }

    private async Task Script(string[] arguments, Action<string> progress, List<string> failures, CancellationToken ct)
    {
        var start = new ProcessStartInfo("node", [Path.Combine(root, "scripts/update-machines.ts"), .. arguments])
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
        if (process.ExitCode != 0) failures.Add(last ?? $"The update exited {process.ExitCode}.");
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
    Task InstallAsync(string machine, Action<string> progress, CancellationToken ct, bool keepAdapters = false);
    /// <summary>Adapters and CLIs on a machine, this one when <paramref name="machine"/> is null, leaving a current backend in place.</summary>
    Task UpdateToolsAsync(string? machine, ToolsBehind tools, Action<string> progress, CancellationToken ct);
}

/// <summary>
/// A delivered app: everything is the same except that wanted versions and the update come from the signed release. A build
/// with no <see cref="IReleaseInstall"/> has nothing to update from.
/// </summary>
internal sealed class ReleaseUpdateSource(CliReleaseSource npm, IReleaseInstall? release, TimeProvider time, IAppInstall? app = null) : IUpdateSource
{
    // A check runs at start and at each remote's hello, and GitHub allows an address 60 unsigned reads an hour.
    private static readonly TimeSpan PublishedLasts = TimeSpan.FromMinutes(10);
    private (PublishedVersions Read, DateTimeOffset At)? _published;

    public bool StartsRemoteUpdates(string machine) => true;
    public bool StartsAppUpdate(string version) => app is not null && app.FailedVersion != version;
    public string? AppNote => app?.Note;
    public string? AppReady => app?.Ready;
    public Task RestartAppAsync(Action<string> progress, CancellationToken ct) =>
        (app ?? throw new CallFailed("No update is ready to restart into.", "no_update")).RestartAsync(progress, ct);
    public void AppStarted() => app?.Started();

    public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) =>
        (release ?? throw new CallFailed("No signed release is published yet, so there's nothing to install.", "no_release")).InstallAsync(machine, progress, ct);

    public async Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false)
    {
        var published = await PublishedAsync(ct, fresh);
        return new(published.App, BackendBuild.Version,
            UpdateSources.AdapterNames.ToDictionary(name => name, name => published.Adapters?.GetValueOrDefault(name), StringComparer.Ordinal),
            published.Packaging, await UpdateSources.CliVersionsAsync(npm, ct));
    }

    private async Task<PublishedVersions> PublishedAsync(CancellationToken ct, bool fresh)
    {
        if (release is null) return new(null, null, null);
        if (!fresh && _published is { } kept && time.GetUtcNow() - kept.At < PublishedLasts) return kept.Read;
        var read = await release.PublishedAsync(ct);
        if (read.App is not null || read.Adapters is not null) _published = (read, time.GetUtcNow());
        return read;
    }

    public async Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
    {
        if (release is null) throw new CallFailed("No signed release is published yet, so there's nothing to update from.", "no_release");
        var failures = new List<string>();
        // A new app waits for its restart, and what's behind on the rest needn't. By itself, only the app and remotes go.
        var behind = machines.FirstOrDefault(machine => machine.Name is null)?.Behind.FirstOrDefault(item => item.Kind == "desktop");
        if (behind?.Wanted is { } wanted && app is not null && app.Ready != wanted)
        {
            try { await app.StageAsync(wanted, progress, ct); }
            catch (CallFailed failed) { failures.Add(failed.Message); }
        }
        await UpdateSources.RemotesAsync(release, automatic ? machines.Where(machine => machine.Name is not null) : machines, progress, failures, ct);
        if (behind is not null && app is null && !automatic) failures.Add($"Assembly {behind.Wanted} is out, and this app can't install it from here: install it from the release page.");
        if (failures.Count > 0) throw new CallFailed(string.Join(" ", failures), "update_failed");
    }
}
