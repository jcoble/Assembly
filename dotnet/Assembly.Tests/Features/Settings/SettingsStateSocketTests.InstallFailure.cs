using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Desktop.Settings;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Persistence;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Features.Chat.Sessions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Features.Settings;

public sealed partial class SettingsStateSocketTests
{
    // A failed adapter update is the provider's own failure (not just a machine warning), with a
    // retry that means "install again"; a checked machine reporting no models disagrees with one
    // reporting some, while an unchecked machine's absence does not vote.
    [Fact]
    public async Task A_failed_provider_update_is_a_provider_failure_with_an_install_retry()
    {
        static SettingsProviderMachine WithModels(IReadOnlyList<ModelChoice>? models) =>
            new("local", null, ProviderRowState.Ready, true, null, null, null, models, null, null, null, null, 0);
        Assert.Null(SettingsStateRules.DeriveSharedModels(
            [WithModels([]), WithModels([new ModelChoice("m", "M")])]));
        Assert.Null(SettingsStateRules.DeriveSharedModels([WithModels(null), WithModels([])]));

        var tempRoot = Directory.CreateTempSubdirectory("assembly-settings-install-").FullName;
        var dbPath = Path.Combine(tempRoot, "assembly.db");
        var topics = new TopicSet();
        var db = AppDatabase.Open(dbPath, TimeProvider.System, topics);
        EngineSchema.Open(db, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        var work = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        var status = new MachineStatus(topics, []);
        var sessions = SessionStore.Open(db, TimeProvider.System, Path.Combine(tempRoot, "sessions"));
        var machinesFeature = new MachinesFeature(new MachineSet(new FakeMachine(null, sessions)), status, () => { }, TimeProvider.System);
        var launcher = new FakeLauncher
        {
            Agents = [new("claude", "Claude Code", "claude-agent-acp", [], null, VendorFamily: "claude")]
        };
        new ProviderEnablementStore(db, TimeProvider.System).SetEnabled("claude", true);
        var feature = new SettingsStateFeature(db, topics, work, launcher, machinesFeature, "local-engine", TimeProvider.System);
        var registry = new TopicCaptureRegistry();
        feature.Register(registry);
        var ownerCall = new CallContext(CancellationToken.None, "owner", Peer: new OwnerPeer());
        try
        {
            status.Updates(null, [new UpdateItem("adapter", "claude-agent-acp", "1.0.0", "2.0.0", UpdateItem.Behind)]);
            status.Updating(null, false, "Update failed: The update didn't finish.");
            var states = (IReadOnlyList<SettingsState>)registry.ReadState!(null, ownerCall);
            var json = JsonSerializer.Serialize(Assert.Single(states), SettingsStateJson.Default.SettingsState);
            using var parsed = JsonDocument.Parse(json);
            var provider = parsed.RootElement.GetProperty("providers").EnumerateArray()
                .Single(p => p.GetProperty("id").GetString() == "claude");
            Assert.Equal(ProviderRowState.Problem, provider.GetProperty("state").GetString());
            var local = provider.GetProperty("machines").EnumerateArray()
                .Single(m => m.GetProperty("machineKey").GetString() == "local");
            Assert.Equal(ProviderRowState.Problem, local.GetProperty("state").GetString());
            var failure = local.GetProperty("lastFailure");
            Assert.Equal("The update didn't finish.", failure.GetProperty("message").GetString());
            Assert.Equal("Update failed: The update didn't finish.", failure.GetProperty("detail").GetString());
            Assert.Equal("install", failure.GetProperty("retry").GetString());
        }
        finally
        {
            await feature.DisposeAsync();
            await machinesFeature.DisposeAsync();
            await work.DisposeAsync();
            db.Dispose();
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
