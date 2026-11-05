using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Updates;

/// <summary>
/// Owns the one check and the one update. The check runs when this app starts, each time a remote says hello, and after an
/// update; there's no timer. Results go out on the machines topic. A failure to read shows as unknown, never as behind.
/// A remote on an older version starts the update by itself, once per run of the app so a failure can't repeat forever; the
/// button can always try again.
/// </summary>
internal sealed class MachineUpdates(IUpdateSource source, MachineStatus status, InstalledCalls local,
    Func<string, CancellationToken, Task<InstalledTools?>> remote, Action? reload, BackgroundWork work, TextWriter log) : IFeature
{
    private const string ThisApp = "";
    private int _updating;
    private readonly HashSet<string> _started = [];

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
        if (Volatile.Read(ref _updating) == 0) status.Updating(machine, false, null);
    }

    /// <summary>The first check, and every remote's check from here on.</summary>
    internal void Begin()
    {
        status.Welcomed = machine => Check(machine);
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

    internal void Check(string? machine) => work.Start("update check", async stop =>
    {
        // Welcomes arrive under the status lock, and the check reads the network.
        await Task.Yield();
        await CheckAsync(machine, stop);
    });

    internal async Task CheckAsync(string? machine, CancellationToken stop, bool fresh = false)
    {
        var wanted = await source.WantedAsync(stop, fresh);
        if (machine is null)
        {
            var here = UpdateCheck.Local(BackendBuild.Version, await TryRead(async () => await local.ReadAsync(stop)), wanted);
            status.Updates(null, here);
            if (here.Any(item => item.Kind == "desktop" && item.State == UpdateItem.Behind && item.Wanted is not null && source.StartsAppUpdate(item.Wanted))) StartBy(ThisApp);
            return;
        }
        var items = UpdateCheck.Remote(status.Items.FirstOrDefault(item => item.Name == machine)?.Backend, await TryRead(() => remote(machine, stop)), wanted);
        status.Updates(machine, items);
        if (source.StartsRemoteUpdates(machine) && items.Any(item => item.Kind == "backend" && item.State == UpdateItem.Behind)) StartBy(machine);
    }

    // A check that arrives during an update starts nothing: the end of that update checks every machine again.
    private void StartBy(string machine)
    {
        lock (_started)
        {
            if (_started.Contains(machine) || !Begin(automatic: true)) return;
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
    /// Takes the same one-at-a-time place as an update.
    /// </summary>
    internal bool Install(string machine)
    {
        if (status.Items.All(item => item.Name != machine)) throw new CallFailed("Unknown remote machine.", "unknown_machine");
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0) return false;
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
                Volatile.Write(ref _updating, 0);
                status.Updating(machine, false, ending);
            }
        });
        return true;
    }

    /// <summary>False when an update is already running: it's the same one, so the button joins it.</summary>
    internal bool Start() => Begin(automatic: false);

    private bool Begin(bool automatic)
    {
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0) return false;
        work.Start("update", stop => RunAsync(automatic, stop));
        return true;
    }

    private async Task RunAsync(bool automatic, CancellationToken stop)
    {
        var machines = status.Items.Select(item => item.Name).ToArray();
        void Show(bool updating, string? note) { foreach (var machine in machines) status.Updating(machine, updating, note); }
        string? ending = null;
        try
        {
            Show(true, null);
            MachineBehind[] behind = [.. status.Items.Select(item => new MachineBehind(item.Name, [.. (item.Updates ?? []).Where(update => update.State == UpdateItem.Behind)],
                (item.Updates ?? []).Any(update => update.Kind == "adapter" && update.State == UpdateItem.Newer)))];
            await source.UpdateAsync(behind, automatic, note => Show(true, note), stop);
        }
        catch (CallFailed error) { ending = "Update failed: " + error.Message; }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { ending = "Update failed: " + error.Message; }
        finally
        {
            Volatile.Write(ref _updating, 0);
            Show(false, ending);
        }
        if (ending is null) Updated();
        else foreach (var machine in machines) Check(machine);
    }

    /// <summary>Adapters may be new: agents are read again, and every machine checked again.</summary>
    private void Updated()
    {
        reload?.Invoke();
        foreach (var machine in status.Items.Select(item => item.Name)) Check(machine);
    }
}
