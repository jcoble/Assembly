using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Updates;

/// <summary>
/// Owns the one check and the one update. The check runs when this app starts, each time a remote says hello, and after an
/// update; there's no timer. Results go out on the machines topic. A failure to read shows as unknown, never as behind.
/// </summary>
internal sealed class MachineUpdates(IUpdateSource source, MachineStatus status, InstalledCalls local,
    Func<string, CancellationToken, Task<InstalledTools?>> remote, Action? reload, BackgroundWork work, TextWriter log) : IFeature
{
    private int _updating;

    public void Register(ICallRegistry calls) => calls.Read("updates", "start", UpdatesJson.Default.InstalledArgs, UpdatesJson.Default.UpdateStarted, (_, call) =>
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("Owner access is required.", "forbidden");
        return Task.FromResult(new UpdateStarted(Start()));
    });

    /// <summary>The first check, and every remote's check from here on.</summary>
    internal void Begin()
    {
        status.Welcomed = machine => Check(machine);
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

    internal async Task CheckAsync(string? machine, CancellationToken stop)
    {
        var wanted = await source.WantedAsync(stop);
        if (machine is null) status.Updates(null, UpdateCheck.Local(BackendBuild.Version, await TryRead(async () => await local.ReadAsync(stop)), wanted));
        else status.Updates(machine, UpdateCheck.Remote(status.Items.FirstOrDefault(item => item.Name == machine)?.Backend,
            await TryRead(() => remote(machine, stop)), wanted));
    }

    private async Task<InstalledTools?> TryRead(Func<Task<InstalledTools?>> read)
    {
        try { return await read(); }
        catch (Exception error) when (error is CallFailed or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { log.WriteLine($"update check: {error.Message}"); return null; }
    }

    /// <summary>False when an update is already running: it's the same one, so the button joins it.</summary>
    internal bool Start()
    {
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0) return false;
        work.Start("update", RunAsync);
        return true;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var machines = status.Items.Select(item => item.Name).ToArray();
        void Show(bool updating, string? note) { foreach (var machine in machines) status.Updating(machine, updating, note); }
        string? ending = null;
        try
        {
            Show(true, null);
            await source.UpdateAsync([.. machines.OfType<string>()], note => Show(true, note), stop);
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
