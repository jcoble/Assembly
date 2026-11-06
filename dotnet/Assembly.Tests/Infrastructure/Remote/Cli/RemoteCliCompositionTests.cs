using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Remote;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class RemoteCliCompositionTests
{
    [Fact]
    public async Task Caught_up_fetch_saves_the_actual_locator_and_offline_lookup_uses_the_saved_engine_profile()
    {
        await using var f = new Fixture();
        var locator = f.Locator();
        f.Remote.AdditionalFeatures = calls => calls.Read("engine", "cli_locators", EngineSocketJson.Default.JsonElement,
            RemoteCliLocatorJson.Default.RemoteCliLocator, (_, _) => Task.FromResult(locator));
        await f.Start();
        Assert.True(await f.Writer.Saved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var saved = f.Connections.CliLocator(f.Local.Db, f.Remote.Fixture.Machine, default)!;
        Assert.Equal(locator.CaptureFile, saved.Locator.CaptureFile);
        Assert.Equal(locator.InstallationId, saved.Locator.InstallationId);
        Assert.Equal(f.Profile, saved.Profile.Profile);
        await f.Connections.ConnectionAsync(f.Profile.Name, false, default);
        Assert.Null(f.Connections.StartupSnapshot(f.Profile.Name));
        Assert.Equal(locator.InstallationId, f.Connections.CliLocator(f.Local.Db, locator.MachineId, default)!.Locator.InstallationId);
        Assert.Null(f.Connections.CliLocator(f.Local.Db, "unknown-engine", default));
        f.Local.Db.Writer.Write("test.retire", scope => scope.Execute("UPDATE engine_mirror SET state='retired' WHERE machine=$machine", new SqlArg("$machine", locator.MachineId)));
        Assert.Null(f.Connections.CliLocator(f.Local.Db, locator.MachineId, default));
    }

    [Fact]
    public async Task Identical_profile_replacement_during_fetch_cannot_publish_the_old_profile_generation()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Remote.AdditionalFeatures = calls => calls.Read("engine", "cli_locators", EngineSocketJson.Default.JsonElement,
            RemoteCliLocatorJson.Default.RemoteCliLocator, async (_, call) =>
            { entered.TrySetResult(); await release.Task.WaitAsync(call.Cancellation); return f.Locator(); });
        await f.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var original = f.Store.CaptureProfile(f.Profile.Name, default)!;
        // Update through the real profile writer without publishing a second connection request.
        // The original fetch remains in flight, so this isolates the actual generation CAS.
        f.Local.Db.Writer.Write("test.replace-profile", scope => scope.Execute(
            "UPDATE saved_machines SET profile_generation=$generation,cli_locator_json=NULL WHERE name=$name",
            new SqlArg("$generation", Guid.NewGuid().ToString("N")), new("$name", f.Profile.Name)));
        release.SetResult();
        Assert.False(await f.Writer.Saved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(original.Generation, f.Store.CaptureProfile(f.Profile.Name, default)!.Generation);
        Assert.Null(f.Store.GetCliLocator(f.Profile.Name, default));
    }

    [Fact]
    public async Task Owner_reconnect_discards_the_inflight_snapshot_then_fetches_the_new_current_owner_once()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var current = f.Locator();
        f.Remote.AdditionalFeatures = calls => calls.Read("engine", "cli_locators", EngineSocketJson.Default.JsonElement,
            RemoteCliLocatorJson.Default.RemoteCliLocator, async (_, call) =>
            {
                var selected = current;
                if (Interlocked.Increment(ref reads) == 1)
                { entered.TrySetResult(); await release.Task.WaitAsync(call.Cancellation); }
                return selected;
            });
        await f.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var old = current;
        current = current with { InstallationId = Guid.NewGuid().ToString("N") };
        f.Remote.Endpoint.DropAll();
        release.TrySetResult();
        Assert.True(await f.Writer.Saved.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        var saved = f.Store.GetCliLocator(f.Profile.Name, default)!;
        Assert.NotEqual(old.InstallationId, saved.Locator.InstallationId);
        Assert.Equal(current.InstallationId, saved.Locator.InstallationId);
        Assert.Equal(2, Volatile.Read(ref reads));
    }

    [Fact]
    public async Task Missing_remote_publication_does_not_invent_a_capture_or_offline_locator()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Remote.AdditionalFeatures = calls => calls.Read("engine", "cli_locators", EngineSocketJson.Default.JsonElement,
            RemoteCliLocatorJson.Default.RemoteCliLocator, (_, _) =>
            {
                entered.TrySetResult();
                throw new CallFailed("CLI configuration unavailable.", "configuration_unavailable");
            });
        await f.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Connections.ConnectionAsync(f.Profile.Name, false, default);
        Assert.Null(f.Store.GetCliLocator(f.Profile.Name, default));
        Assert.Null(f.Connections.CliLocator(f.Local.Db, f.Remote.Fixture.Machine, default));
    }

    [Fact]
    public async Task Disposal_cancels_and_joins_an_unavailable_locator_fetch()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Remote.AdditionalFeatures = calls => calls.Read("engine", "cli_locators", EngineSocketJson.Default.JsonElement,
            RemoteCliLocatorJson.Default.RemoteCliLocator, async (_, call) =>
            {
                entered.TrySetResult();
                try { await new TaskCompletionSource().Task.WaitAsync(call.Cancellation); }
                finally { cancelled.TrySetResult(); }
                return f.Locator();
            });
        await f.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Connections.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(f.Store.GetCliLocator(f.Profile.Name, default));
    }

    private sealed class ObservedWriter(IAtomicWriter inner) : IAtomicWriter
    {
        internal TaskCompletionSource<bool> Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public T Write<T>(string what, Func<IWriteScope, T> write)
        {
            var result = inner.Write(what, write);
            if (what == "machines.cli-locator") Saved.TrySetResult((bool)(object)result!);
            return result;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly EngineFixture Local;
        internal readonly TwoHosts.Host Remote = new();
        internal readonly SavedMachine Profile = new("saved-box", "workbox-test");
        internal MachineStore Store { get; }
        internal RemoteConnections Connections { get; }
        internal ObservedWriter Writer { get; }
        private readonly LiveHub _hub;
        private readonly MachinesFeature _feature;
        internal Fixture()
        {
            var topics = new Assembly.Infrastructure.Topics.Topics();
            Local = new(topics: topics);
            _hub = new(RowStore.Open(Local.Db));
            var sessions = SessionStore.Open(Local.Db, Local.Time, "checkouts");
            var machines = new MachineSet(new FakeMachine(null, sessions));
            var status = new MachineStatus(topics, []);
            Store = new(Local.Db);
            Writer = new(Local.Db.Writer);
            _feature = new(machines, status, () => { }, TimeProvider.System);
            Connections = new(Store, Writer, machines, status, _hub, sessions, topics, TextWriter.Null, _feature,
                open: (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult<(RemoteConfig, IAsyncDisposable?)>((new(Profile.Name, Remote.Address, "isolated-token"), null)); });
        }
        internal RemoteCliLocator Locator() => new(Remote.Fixture.Machine, Guid.NewGuid().ToString("N"),
            "/owner-selected/capture", "/installed/assembly-remote", [], "/installed", []);
        internal async Task Start()
        {
            await Remote.Start();
            Connections.Start(Profile);
            await Connections.ReadyAsync(Profile.Name);
        }
        public async ValueTask DisposeAsync()
        {
            await Connections.DisposeAsync(); await _feature.DisposeAsync(); _hub.Dispose();
            await Remote.DisposeAsync(); Local.Dispose();
        }
    }
}
