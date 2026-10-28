using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Tests.Draft.Base;
using Assembly.Tests.Draft.Base.Chat;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class RemoteConnectionsTests
{
    [Fact]
    public async Task Profiles_defaults_updates_and_removal_survive_reopening_SQLite()
    {
        using var file = new TempDatabase();
        using (var db = TestDatabase.Open(file.Path))
        {
            var store = new MachineStore(db);
            store.Import(new("box", "first"));
            store.Import(new("second", "other", 9837, "other/env"));
            store.Import(new("box", "edited", 9847, "edited/env"));
            db.Writer.Write("test.remove", scope => { store.Remove(scope, "second"); return 0; });
        }
        using (var db = TestDatabase.Open(file.Path))
        {
            var profiles = new MachineStore(db).All(CancellationToken.None);
            Assert.Equal([new SavedMachine("box", "edited", 9847, "edited/env")], profiles);
            new MachineStore(db).Import(new("defaults", "host"));
        }
        using var reopened = TestDatabase.Open(file.Path);
        Assert.Equal(new SavedMachine("defaults", "host"), new MachineStore(reopened).Get("defaults", CancellationToken.None));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Runtime_registration_restart_disconnect_reconnect_and_removal_route_existing_sessions()
    {
        using var file = new TempDatabase();
        await using var first = new MachinesSocketTests.StubRemote();
        await using var second = new MachinesSocketTests.StubRemote();
        await first.StartAsync();
        await second.StartAsync();
        for (var restart = 0; restart < 2; restart++)
        {
            var topics = new Topics();
            using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
            var rows = RowStore.Open(db);
            using var hub = new LiveHub(rows);
            var sessions = SessionStore.Open(db, TimeProvider.System, "checkouts");
            var local = new FakeMachine(null, sessions);
            var machines = new MachineSet(local);
            var status = new MachineStatus(topics, []);
            var store = new MachineStore(db);
            var opens = 0;
            await using var feature = new MachinesFeature(machines, status, () => { }, TimeProvider.System);
            await using var connections = new RemoteConnections(store, machines, status, hub, sessions, topics, TextWriter.Null,
                feature, open: (profile, cancel) =>
                {
                    cancel.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref opens);
                    return Task.FromResult<(RemoteConfig, IAsyncDisposable?)>((new(profile.Name, profile.Ssh == "first" ? first.Address : second.Address, "token"), null));
                });
            connections.Start();
            if (restart == 0)
            {
                store.Import(new("box", "first"));
                sessions.Add("existing", "claude", "repo", "box");
            }
            await connections.ConnectionAsync("box", true, CancellationToken.None);
            var session = sessions.Get("existing")!;
            Assert.Equal("remote", (await machines.For(session).ConfigAsync(session, CancellationToken.None)).Choices["where"]);
            Assert.Equal("remote", (await machines.For(session).SetConfigAsync(session, "model", "chosen", CancellationToken.None)).Choices["where"]);
            Assert.Equal("remote-run", await machines.For(session).SendAsync(session, "hello", null, CancellationToken.None));
            Assert.Empty(local.Asked);
            await connections.ConnectionAsync("box", false, CancellationToken.None);
            var disconnectedOpens = opens;
            Assert.Throws<MachineUnreachable>(() => machines.For(session));
            Assert.Equal("disconnected", status.Items.Single(item => item.Name == "box").Reason);
            Assert.NotNull(store.Get("box", CancellationToken.None));
            Assert.Equal(disconnectedOpens, opens);
            store.Import(new("box", "second"));
            await connections.ConnectionAsync("box", true, CancellationToken.None);
            Assert.Equal("remote-run", await machines.For(session).SendAsync(session, "edited", null, CancellationToken.None));
            Assert.True(second.SendHits > 0);
            if (restart == 0) await connections.ConnectionAsync("box", false, CancellationToken.None);
            if (restart == 1)
            {
                db.Writer.Write("test.remove", scope => { store.Remove(scope, "box"); return 0; });
                // Connect is rejected once the committed profile is gone; shutdown awaits the queued removal.
                await Assert.ThrowsAsync<Assembly.Application.Common.Calls.CallFailed>(async () => await connections.ConnectionAsync("box", true, CancellationToken.None));
                await connections.DisposeAsync();
                Assert.Throws<MachineUnreachable>(() => machines.For(session));
                Assert.NotNull(sessions.Get("existing"));
                Assert.Empty(local.Asked);
            }
        }
        using var final = TestDatabase.Open(file.Path);
        Assert.Empty(new MachineStore(final).All(CancellationToken.None));
    }

    [Fact]
    public async Task Import_flags_win_each_start_preserve_other_profiles_and_recreate_removed_profile()
    {
        using var file = new TempDatabase();
        for (var launch = 0; launch < 3; launch++)
        {
            var topics = new Topics();
            using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
            var store = new MachineStore(db);
            if (launch == 0) store.Import(new("other", "untouched"));
            var rows = RowStore.Open(db);
            using var hub = new LiveHub(rows);
            var sessions = SessionStore.Open(db, TimeProvider.System, "checkouts");
            var machines = new MachineSet(new FakeMachine(null, sessions));
            var status = new MachineStatus(topics, []);
            var attempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var feature = new MachinesFeature(machines, status, () => { }, TimeProvider.System);
            await using var connections = new RemoteConnections(store, machines, status, hub, sessions, topics, TextWriter.Null,
                feature, open: async (profile, cancel) =>
                {
                    attempting.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancel);
                    throw new InvalidOperationException();
                });
            var options = global::Assembly.Desktop.Serve.DesktopOptions.Parse(["--remote", "workbox-test", "--remote-port", (9837 + launch).ToString(), "--remote-token-file", $"test/{launch}"])!;
            var expected = new SavedMachine(options.Remote!, options.Remote!, options.RemotePort, options.RemoteTokenFile);
            connections.Start(expected);
            // Startup returns even though a connection attempt cannot finish.
            await attempting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, store.Get("workbox-test", CancellationToken.None));
            Assert.NotNull(store.Get("other", CancellationToken.None));
            Assert.NotNull(machines.Named(null));
            if (launch == 1) db.Writer.Write("test.remove", scope => { store.Remove(scope, "workbox-test"); return 0; });
        }
    }

    [Theory]
    [InlineData("update")]
    [InlineData("disconnect")]
    [InlineData("remove")]
    public async Task Old_connection_work_is_disposed_and_cannot_publish_for_its_replacement(string operation)
    {
        using var file = new TempDatabase();
        var topics = new Topics();
        using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        var rows = RowStore.Open(db);
        using var hub = new LiveHub(rows);
        var sessions = SessionStore.Open(db, TimeProvider.System, "checkouts");
        sessions.Add("running", "claude", "repo", "box");
        sessions.Add("helper", "codex", "repo", "box", "running", helper: true);
        // The copied unfinished turn must be followed again after a connect.
        db.Writer.Write("test.running", scope => scope.Execute("INSERT INTO rows(session_id, seq, run_id, kind, payload, created_at) VALUES ('running', 1, 'r1', 'turn', '{\"state\":\"started\"}', 1); INSERT INTO rows(session_id, seq, run_id, item_id, kind, payload, created_at) VALUES ('running', 2, 'r1', 'helper-item', 'subagent', '{\"sessionId\":\"helper\",\"state\":\"running\"}', 2);"));
        await using var stub = new MachinesSocketTests.StubRemote();
        await stub.StartAsync();
        var machines = new MachineSet(new FakeMachine(null, sessions));
        var status = new MachineStatus(topics, []);
        var store = new MachineStore(db);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var feature = new MachinesFeature(machines, status, () => { }, TimeProvider.System);
        await using var connections = new RemoteConnections(store, machines, status, hub, sessions, topics, TextWriter.Null,
            feature, open: (profile, cancel) =>
                Task.FromResult<(RemoteConfig, IAsyncDisposable?)>((new(profile.Name, stub.Address, "token"), new DisposalReceipt(disposed))));
        store.Import(new("box", "host"));
        await connections.ConnectionAsync("box", true, CancellationToken.None);
        await stub.LiveSeenAsync("running").WaitAsync(TimeSpan.FromSeconds(5));
        await stub.LiveSeenAsync("helper").WaitAsync(TimeSpan.FromSeconds(5));
        if (operation == "update")
        {
            store.Import(new("box", "edited"));
            await connections.ConnectionAsync("box", true, CancellationToken.None);
        }
        else if (operation == "disconnect") await connections.ConnectionAsync("box", false, CancellationToken.None);
        else db.Writer.Write("test.remove", scope => { store.Remove(scope, "box"); return 0; });
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connections.DisposeAsync();
        await stub.NoLiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, stub.LiveOpen);
        Assert.NotNull(sessions.Get("running"));
        Assert.Equal(2, rows.RowsAfter("running", 0).Count);
        var oldStatus = status.Scope("box");
        oldStatus.Stop();
        var before = status.Items;
        oldStatus.Unreachable("box", "stale callback");
        Assert.Same(before, status.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profile_writes_retire_old_status_callbacks_before_the_committed_change(bool remove)
    {
        using var file = new TempDatabase();
        var topics = new Topics();
        using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        var status = new MachineStatus(topics, []);
        var store = new MachineStore(db) { Status = status };
        store.Import(new("box", "old"));
        status.Profile(new("box", "old"));
        var old = status.Scope("box");
        var published = new List<string>();
        var subscription = topics.Subscribe("machines", null, _ => { }, (_, json) => published.Add(json));
        if (remove) db.Writer.Write("test.remove", scope => { store.Remove(scope, "box"); return 0; });
        else store.Import(new("box", "new"));
        old.Reachable("box");
        old.Unreachable("box", "late failure");
        status.ForProbe("box").Unreachable("box", "late probe timeout");
        Assert.Single(published);
        if (remove) Assert.Equal("null", published[0]);
        else Assert.Contains("\"ssh\":\"new\"", published[0]);
        topics.Unsubscribe(subscription);
    }

    private sealed class DisposalReceipt(TaskCompletionSource done) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { done.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
