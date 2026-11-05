using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Updates;

/// <summary>
/// Owns the check and the updates. The check runs when this app starts, each time a remote says hello, and after an
/// update; there's no timer. Results go out on the machines topic. A failure to read shows as unknown, never as behind, and a
/// machine that isn't answering has nothing known about it. Each machine updates by itself, so one waiting on its sessions
/// holds up nothing else, and says so on its own card. A remote on an older version starts its update by itself, once per run
/// of the app so a failure can't repeat forever; the button can always try again. An app that's behind gets ready the same
/// way, and restarts only when someone says so.
/// </summary>
internal sealed class MachineUpdates(IUpdateSource source, MachineStatus status, InstalledCalls local,
    Func<string, CancellationToken, Task<InstalledTools?>> remote, Action? reload, BackgroundWork work, TextWriter log) : IFeature
{
    private const string ThisApp = "";
    private readonly HashSet<string> _updating = [];
    private readonly HashSet<string> _started = [];

    private static string Slot(string? machine) => machine ?? ThisApp;

    private bool Take(string? machine) { lock (_updating) return _updating.Add(Slot(machine)); }
    private void Free(string? machine) { lock (_updating) _updating.Remove(Slot(machine)); }
    private bool Updating(string? machine) { lock (_updating) return _updating.Contains(Slot(machine)); }

    public void Register(ICallRegistry calls)
    {
        calls.Read("updates", "start", UpdatesJson.Default.InstalledArgs, UpdatesJson.Default.UpdateStarted, (_, call) =>
        {
            if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden");
            return Task.FromResult(new UpdateStarted(Start()));
        });
        calls.Read("updates", "install", UpdatesJson.Default.InstallArgs, UpdatesJson.Default.UpdateStarted, (args, call) =>
        {
            if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden");
            return Task.FromResult(new UpdateStarted(Install(args.Machine)));
        });
        calls.Read("updates", "restart", UpdatesJson.Default.InstalledArgs, UpdatesJson.Default.UpdateStarted, (_, call) =>
        {
            if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden");
            return Task.FromResult(new UpdateStarted(RestartApp()));
        });
        calls.Read("updates", "appStarted", UpdatesJson.Default.InstalledArgs, UpdatesJson.Default.UpdateChecked, (_, call) =>
        {
            if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden");
            source.AppStarted();
            return Task.FromResult(new UpdateChecked(true));
        });
        calls.Read("updates", "check", UpdatesJson.Default.InstallArgs, UpdatesJson.Default.UpdateChecked, async (args, call) =>
        {
            if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden");
            await CheckNowAsync(args.Machine, call.Cancellation);
            return new UpdateChecked(true);
        });
    }

    /// <summary>
    /// The same check, because someone asked: it reads the release again rather than what was kept, and an old failure gives
    /// way to the answer. It installs nothing itself; a remote on an older version updates by itself, as after any check.
    /// </summary>
    internal async Task CheckNowAsync(string machine, CancellationToken stop)
    {
        if (status.Items.All(item => item.Name != machine)) throw new CallFailed("Unknown remote machine.", "unknown_machine");
        await CheckAsync(machine, stop, fresh: true);
        if (!Updating(machine)) status.Updating(machine, false, null);
    }

    /// <summary>The first check, and every remote's check from here on.</summary>
    internal void Begin()
    {
        status.Welcomed = machine => Check(machine, answered: true);
        if (source.AppNote is { } note) status.Updating(null, false, note);
        // app-dev runs the launch's update itself, so it can start before the desktop is built, and tells this app how it goes.
        if (Environment.GetEnvironmentVariable(DevRemoteSyncStatus.Variable) is { Length: > 0 } path) work.Start("launch update status", async stop =>
        {
            using var watcher = new DevRemoteSyncStatus(path, status, Updated);
            await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        });
        Check(null);
        foreach (var item in status.Items.Where(item => item.Name is not null && item.Backend is not null)) Check(item.Name);
    }

    internal void Check(string? machine, bool answered = false) => work.Start("update check", async stop =>
    {
        // Welcomes arrive under the status lock, and the check reads the network.
        await Task.Yield();
        await CheckAsync(machine, stop, answered: answered);
    });

    /// <param name="machine">Null is this app.</param>
    /// <param name="stop">Stops the reads.</param>
    /// <param name="fresh">Someone asked, so nothing kept answers for the release.</param>
    /// <param name="answered">It just said hello, which comes before its status says it's reachable.</param>
    internal async Task CheckAsync(string? machine, CancellationToken stop, bool fresh = false, bool answered = false)
    {
        var wanted = await source.WantedAsync(stop, fresh);
        if (machine is null)
        {
            var here = UpdateCheck.Local(BackendBuild.Version, await TryRead(async () => await local.ReadAsync(stop)), wanted);
            status.Updates(null, here);
            if (here.Any(item => item.Kind == "desktop" && item.State == UpdateItem.Behind && item.Wanted is not null && source.StartsAppUpdate(item.Wanted))) StartBy(ThisApp);
            return;
        }
        var saved = status.Items.FirstOrDefault(item => item.Name == machine);
        // What an unreachable machine last said is old news: a decision from it could install over a newer backend.
        var reachable = answered || saved?.Reachable == true;
        var items = UpdateCheck.Remote(reachable ? saved?.Backend : null, await TryRead(() => remote(machine, stop)), wanted);
        status.Updates(machine, items);
        if (reachable && source.StartsRemoteUpdates(machine) && items.Any(item => item.Kind == "backend" && item.State == UpdateItem.Behind)) StartBy(machine);
    }

    // A check that arrives during an update starts nothing: the end of that update checks every machine again.
    private void StartBy(string machine)
    {
        lock (_started)
        {
            if (_started.Contains(machine) || status.Items.FirstOrDefault(item => item.Name == (machine == ThisApp ? null : machine)) is not { } item || !Begin(item, automatic: true)) return;
            _started.Add(machine);
        }
    }

    private async Task<InstalledTools?> TryRead(Func<Task<InstalledTools?>> read)
    {
        try { return await read(); }
        catch (Exception error) when (error is CallFailed or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { log.WriteLine($"update check: {error.Message}"); return null; }
    }

    /// <summary>
    /// A saved machine with no remote gets one, and then it's in the normal rule: its hello is checked like any other's.
    /// Takes the same place as that machine's update.
    /// </summary>
    internal bool Install(string machine)
    {
        if (status.Items.All(item => item.Name != machine)) throw new CallFailed("Unknown remote machine.", "unknown_machine");
        if (!Take(machine)) return false;
        work.Start("remote install", async stop =>
        {
            string? ending = null;
            try
            {
                status.Updating(machine, true, null);
                await source.InstallAsync(machine, note => status.Updating(machine, true, note), stop);
            }
            catch (CallFailed error) { ending = "Install failed: " + error.Message; }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { ending = "Install failed: " + error.Message; }
            finally
            {
                Free(machine);
                status.Updating(machine, false, ending);
            }
        });
        return true;
    }

    /// <summary>
    /// The only thing that restarts this app. A remote waiting on its sessions doesn't hold it up. False only while this app
    /// is already busy, which its button shows. It waits for this app's sessions; a failure leaves the update ready to press again.
    /// </summary>
    internal bool RestartApp()
    {
        if (source.AppReady is null) throw new CallFailed("No update is ready to restart into.", "no_update");
        if (!Take(null)) return false;
        work.Start("restart to update", async stop =>
        {
            string? ending = null;
            try
            {
                status.Updating(null, true, null);
                await source.RestartAppAsync(note => status.Updating(null, true, note), stop);
            }
            catch (CallFailed error) { ending = "Update failed: " + error.Message; }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { ending = "Update failed: " + error.Message; }
            finally
            {
                Free(null);
                status.Updating(null, false, ending);
            }
        });
        return true;
    }

    /// <summary>
    /// Everything behind on every machine that's answering. False when there was nothing to start: what's running already is
    /// the same update, so the button joins it.
    /// </summary>
    internal bool Start()
    {
        var started = false;
        foreach (var item in status.Items.Where(item => (item.Name is null || item.Reachable) && (item.Updates ?? []).Any(update => update.State == UpdateItem.Behind)))
            started |= Begin(item, automatic: false);
        return started;
    }

    private bool Begin(MachineItem item, bool automatic)
    {
        if (!Take(item.Name)) return false;
        work.Start("update", stop => RunAsync(item, automatic, stop));
        return true;
    }

    private async Task RunAsync(MachineItem item, bool automatic, CancellationToken stop)
    {
        var machine = item.Name;
        string? ending = null;
        try
        {
            status.Updating(machine, true, null);
            MachineBehind behind = new(machine, [.. (item.Updates ?? []).Where(update => update.State == UpdateItem.Behind)],
                (item.Updates ?? []).Any(update => update.Kind == "adapter" && update.State == UpdateItem.Newer));
            await source.UpdateAsync([behind], automatic, note => status.Updating(machine, true, note), stop);
        }
        catch (CallFailed error) { ending = "Update failed: " + error.Message; }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { ending = "Update failed: " + error.Message; }
        finally
        {
            Free(machine);
            if (machine is null) status.RestartReady(source.AppReady);
            status.Updating(machine, false, ending);
        }
        if (ending is null) Updated();
        else Check(machine);
    }

    /// <summary>Adapters may be new: agents are read again, and every machine checked again.</summary>
    private void Updated()
    {
        reload?.Invoke();
        foreach (var machine in status.Items.Select(item => item.Name)) Check(machine);
    }
}
