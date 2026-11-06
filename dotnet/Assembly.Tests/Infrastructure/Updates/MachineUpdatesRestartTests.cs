using Assembly.Application.Common;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Agents;
using Assembly.Infrastructure.Topics;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Updates;

/// <summary>Restart to Update apart from this computer's tools, and a machine this app can't update.</summary>
public sealed class MachineUpdatesRestartTests
{
    private sealed class Source : IUpdateSource
    {
        internal readonly TaskCompletionSource Tools = new();
        internal readonly TaskCompletionSource RestartHeld = new();
        internal int Restarts, Updates;
        internal string? RestartFails;
        internal bool Lane;
        public string? AppReady => "9.0.0";
        public bool IgnoresTokenFile(string machine) => Lane;
        public async Task RestartAppAsync(Action<string> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Restarts);
            if (RestartFails is { } reason) throw new Assembly.Application.Common.Calls.CallFailed(reason);
            progress("Waiting for running sessions to finish…");
            await RestartHeld.Task;
        }
        public Task<UpdateWanted> WantedAsync(CancellationToken ct, bool fresh = false) => Task.FromResult(new UpdateWanted(null, "2.0.0",
            new Dictionary<string, string?>(), "sources", new Dictionary<string, string?>()));
        public bool StartsRemoteUpdates(string machine) => true;
        public Task InstallAsync(string machine, Action<string> progress, CancellationToken ct) => Task.CompletedTask;
        public async Task UpdateAsync(IReadOnlyList<MachineBehind> machines, bool automatic, Action<string> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Updates);
            progress("Updating codex…");
            await Tools.Task;
        }
    }

    private static MachineUpdates Updates(Source source, MachineStatus status, BackgroundWork work) =>
        new(source, status, new InstalledCalls(new AgentsOnlyLauncher(() => []), TimeProvider.System),
            (_, _) => Task.FromResult<InstalledTools?>(null), null, work, TextWriter.Null);

    private static async Task Until(Func<bool> done)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done()) await Task.Delay(5, deadline.Token);
    }

    private static MachineItem Here(MachineStatus status) => status.Items[0];

    private static void ToolsBehind(MachineStatus status) => status.Updates(null, [new("cli", "codex", "1.0.0", "2.0.0", UpdateItem.Behind)]);

    [Fact]
    public async Task A_press_during_a_tools_update_says_what_it_waits_for_then_restarts_and_the_tools_note_stays_off_it()
    {
        var status = new MachineStatus(new Topics(), []);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new Source();
        var updates = Updates(source, status, work);
        ToolsBehind(status);
        Assert.True(updates.Start());
        await Until(() => Here(status).UpdateNote == "Updating codex…");

        Assert.True(updates.RestartApp());
        await Until(() => Here(status).RestartNote == MachineUpdates.WaitingForTools);
        Assert.True(Here(status).Restarting);
        Assert.Equal(0, source.Restarts);
        Assert.Equal("Updating codex…", Here(status).UpdateNote);
        Assert.False(updates.RestartApp());

        source.Tools.SetResult();
        await Until(() => source.Restarts == 1);
        await Until(() => Here(status).RestartNote == "Waiting for running sessions to finish…");
        Assert.False(Here(status).Updating);

        source.RestartHeld.SetResult();
        await Until(() => !Here(status).Restarting);
        Assert.Null(Here(status).RestartNote);
    }

    [Fact]
    public async Task A_failed_tools_update_stays_on_the_card_and_never_on_the_restart_and_a_failed_restart_is_its_own()
    {
        var status = new MachineStatus(new Topics(), []);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new Source { RestartFails = "the disk is full" };
        var updates = Updates(source, status, work);
        ToolsBehind(status);
        Assert.True(updates.Start());
        await Until(() => Here(status).Updating);
        source.Tools.SetException(new IOException("no network"));
        await Until(() => !Here(status).Updating);
        Assert.Equal("Update failed: no network", Here(status).UpdateNote);
        Assert.Null(Here(status).RestartNote);

        Assert.True(updates.RestartApp());
        await Until(() => Here(status).RestartNote == "Couldn't restart: the disk is full" && !Here(status).Restarting);
        Assert.Equal("Update failed: no network", Here(status).UpdateNote);

        // The press is over, so it can be pressed again.
        Assert.True(updates.RestartApp());
        await Until(() => source.Restarts == 2);
    }

    private static MachineStatus SavedWithOldToken(out string name)
    {
        name = "old";
        var status = new MachineStatus(new Topics(), []);
        status.Profile(new SavedMachine(name, name, 9797, ".config/old-install/env"));
        status.Backend(name, new("a", "b", true, "1.0.0", "2.0.0"));
        status.Reachable(name);
        return status;
    }

    [Fact]
    public async Task A_machine_saved_with_another_token_file_is_offered_nothing_started_on_nothing_and_says_why()
    {
        var status = SavedWithOldToken(out var name);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new Source();
        var updates = Updates(source, status, work);

        await updates.CheckAsync(name, default);

        var item = status.Items.Single(machine => machine.Name == name);
        Assert.Contains(".config/old-install/env", item.CantUpdate, StringComparison.Ordinal);
        Assert.DoesNotContain(item.Updates!, update => update.State == UpdateItem.Behind);
        Assert.Contains(item.Updates!, update => update.Kind == "backend" && update.State == UpdateItem.Unknown);
        Assert.False(item.Updating);
        Assert.Null(item.UpdateNote);
        Assert.False(updates.Start());
        await work.DisposeAsync();
        Assert.Equal(0, source.Updates);

        // A check someone asks for leaves the reason where it is.
        await updates.CheckNowAsync(name, default);
        Assert.NotNull(status.Items.Single(machine => machine.Name == name).CantUpdate);
    }

    [Fact]
    public async Task Saving_the_install_s_own_token_file_takes_the_reason_away()
    {
        var status = SavedWithOldToken(out var name);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var updates = Updates(new Source(), status, work);

        status.Profile(new SavedMachine(name, name));

        Assert.Null(status.Items.Single(machine => machine.Name == name).CantUpdate);
        await updates.CheckAsync(name, default);
    }

    [Fact]
    public async Task A_lane_s_own_remote_is_not_held_to_its_saved_token_file()
    {
        var status = SavedWithOldToken(out var name);
        await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var source = new Source { Lane = true };
        source.Tools.SetResult();
        var updates = Updates(source, status, work);

        await updates.CheckAsync(name, default);

        var item = status.Items.Single(machine => machine.Name == name);
        Assert.Null(item.CantUpdate);
        Assert.Contains(item.Updates!, update => update.Kind == "backend" && update.State == UpdateItem.Behind);
    }
}
