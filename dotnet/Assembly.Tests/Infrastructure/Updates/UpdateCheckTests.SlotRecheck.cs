using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Topics;
using Assembly.Infrastructure.Updates;

namespace Assembly.Tests.Infrastructure.Updates;

public sealed partial class UpdateCheckTests
{
    // The reconciler's tools update can wait behind the legacy update for the one local slot; by
    // the time the slot frees, that first pass may already have done the work it selected earlier.
    [Fact]
    public async Task A_local_tools_update_waiting_for_the_slot_rechecks_and_skips_work_already_done()
    {
        var bundle = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(bundle, InstalledCalls.Manifest),
                "{\"versions\":{\"adapter-acp\":\"2.0.0\"},\"packaging\":\"sources\"}");
            var status = new MachineStatus(new Topics(), []);
            await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = 0;
            var source = new FakeSource(async (_, ct) =>
            {
                Interlocked.Increment(ref runs);
                await finish.Task.WaitAsync(ct);
            });
            IReadOnlyList<AgentDescriptor> agents = [];
            var updates = new MachineUpdates(source, status,
                new InstalledCalls(new AgentsOnlyLauncher(() => agents), TimeProvider.System),
                (_, _) => Task.FromResult<InstalledTools?>(Tools()), null, work, TextWriter.Null);
            var item = Item("adapter", "adapter-acp", "2.0.0");
            status.Updates(null, [item]);
            Assert.True(updates.Start());
            await Until(() => Volatile.Read(ref runs) == 1);

            var waiting = updates.UpdateLocalToolsAsync([item], adaptersNewer: false, repair: false, default);
            // The first pass installed the wanted bundle; the reloaded agents now point at it.
            agents = [new AgentDescriptor("a", "A", "node", [Path.Combine(bundle, "node_modules/x/dist/index.js")], null)];
            finish.SetResult();

            Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, Volatile.Read(ref runs));
        }
        finally { Directory.Delete(bundle, true); }
    }

    // The other side of the recheck: an explicit repair (a provider's descriptor or entry point is
    // missing) is not done work just because the shared bundle reads current through the provider
    // that remains. The recheck cannot prove a missing descriptor back, so the repair still runs.
    [Fact]
    public async Task A_local_tools_repair_waiting_for_the_slot_still_runs_when_the_bundle_reads_current()
    {
        var bundle = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(bundle, InstalledCalls.Manifest),
                "{\"versions\":{\"adapter-acp\":\"2.0.0\"},\"packaging\":\"sources\"}");
            var status = new MachineStatus(new Topics(), []);
            await using var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = 0;
            var source = new FakeSource(async (_, ct) =>
            {
                Interlocked.Increment(ref runs);
                await finish.Task.WaitAsync(ct);
            });
            // The remaining provider still points at the current bundle, so a fresh check reads current.
            IReadOnlyList<AgentDescriptor> agents =
                [new AgentDescriptor("a", "A", "node", [Path.Combine(bundle, "node_modules/x/dist/index.js")], null)];
            var updates = new MachineUpdates(source, status,
                new InstalledCalls(new AgentsOnlyLauncher(() => agents), TimeProvider.System),
                (_, _) => Task.FromResult<InstalledTools?>(Tools()), null, work, TextWriter.Null);
            status.Updates(null, [Item("adapter", "adapter-acp", "2.0.0")]);
            Assert.True(updates.Start());
            await Until(() => Volatile.Read(ref runs) == 1);

            // The repair the reconciler selects for a missing descriptor: no wanted version of its own.
            var repair = new UpdateItem("adapter", "adapter-acp", null, null, UpdateItem.Behind);
            var waiting = updates.UpdateLocalToolsAsync([repair], adaptersNewer: false, repair: true, default);
            finish.SetResult();

            Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, Volatile.Read(ref runs));
        }
        finally { Directory.Delete(bundle, true); }
    }
}
