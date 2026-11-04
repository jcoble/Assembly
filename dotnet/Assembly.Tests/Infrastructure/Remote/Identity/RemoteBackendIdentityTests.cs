using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class RemoteBackendIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_different_backend_no_longer_refuses_a_new_remote_session(bool bulk)
    {
        using var file = new TempDatabase();
        var topics = new Topics();
        using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        using var hub = new LiveHub(RowStore.Open(db));
        var sessions = SessionStore.Open(db, TimeProvider.System, file.Folder);
        var status = new MachineStatus(topics, ["box"]);
        var calls = new List<string>();
        await using var remote = new MachinesSocketTests.StubRemote
        {
            Commit = new string('a', 40),
            BackendIdentity = new string('0', 64),
            Reply = (_, method) => { calls.Add(method); return "{}"; }
        };
        await remote.StartAsync();
        await using var machine = new RemoteMachine("box", remote.Address, "box-token", hub, sessions, TextWriter.Null, status);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await machine.ProbeAsync(ct.Token);
        foreach (var (feature, method, json) in new[] {
            ("sessions", "create", "{}"), ("sessions", "create_at_root", "{}"),
            ("engine", "machine_command", """{"command":{"kind":"start_session"}}"""),
            ("engine", "start_title_begin", "{}"), ("engine", "start_title_commit", "{}") })
        {
            using var start = JsonDocument.Parse(json);
            // The stub answers its engine calls with not_found: reaching it is what shows the desktop didn't refuse.
            if (feature == "engine")
                Assert.Equal("Unknown engine call", (await Assert.ThrowsAsync<Assembly.Application.Common.Calls.CallFailed>(() => machine.CallAsync(feature, method, start.RootElement, bulk, ct.Token))).Message);
            else
            {
                await machine.CallAsync(feature, method, start.RootElement, bulk, ct.Token);
                Assert.Contains(method, calls);
            }
        }
        var item = Assert.Single(status.Items, item => item.Name == "box");
        Assert.True(item.Reachable);
        Assert.Null(item.Reason);
        Assert.Equal(new RemoteBackend(remote.Commit, BackendBuild.Commit, true, BackendBuild.Version, BackendBuild.Version), item.Backend);
    }

    [Fact]
    public async Task The_machine_shows_its_commit_and_any_difference_through_the_state_push()
    {
        using var file = new TempDatabase();
        var topics = new Topics();
        using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        using var hub = new LiveHub(RowStore.Open(db));
        var sessions = SessionStore.Open(db, TimeProvider.System, file.Folder);
        var status = new MachineStatus(topics, ["box"]);
        var pushed = new List<MachineItem>();
        using var watching = topics.Subscribe(MachinesFeature.Topic, "box", _ => { }, (_, json) => pushed.Add(JsonSerializer.Deserialize(json, SessionsJson.Default.MachineItem)!));
        await using var same = new MachinesSocketTests.StubRemote { Commit = new string('a', 40) };
        await same.StartAsync();
        await using (var machine = new RemoteMachine("box", same.Address, "box-token", hub, sessions, TextWriter.Null, status))
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await machine.ProbeAsync(ct.Token);
        }
        var shown = Assert.Single(status.Items, item => item.Name == "box").Backend!;
        Assert.Equal(new string('a', 40), shown.Commit);
        Assert.False(shown.Differs);
        Assert.False(shown.VersionDiffers);
        Assert.Contains(pushed, item => item.Backend == shown);

        await using var released = new MachinesSocketTests.StubRemote { Commit = new string('b', 40), BackendIdentity = new string('0', 64), Version = "0.0.1" };
        await released.StartAsync();
        await using (var machine = new RemoteMachine("box", released.Address, "box-token", hub, sessions, TextWriter.Null, status))
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await machine.ProbeAsync(ct.Token);
        }
        shown = Assert.Single(status.Items, item => item.Name == "box").Backend!;
        Assert.True(shown.Differs);
        Assert.True(shown.VersionDiffers);
        Assert.Equal("0.0.1", shown.Version);
        Assert.Contains(pushed, item => item.Backend == shown);
    }

    [Fact]
    public async Task A_host_that_is_down_publishes_failure_without_hanging_or_losing_startup_detail()
    {
        using var file = new TempDatabase();
        var topics = new Topics();
        using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        using var hub = new LiveHub(RowStore.Open(db));
        var sessions = SessionStore.Open(db, TimeProvider.System, file.Folder);
        var status = new MachineStatus(topics, ["box"]);
        status.Deployment("box", "Remote backend failed to start.");
        var remote = new MachinesSocketTests.StubRemote();
        await remote.StartAsync();
        var address = remote.Address;
        await remote.DisposeAsync();
        await using var machine = new RemoteMachine("box", address, "box-token", hub, sessions, TextWriter.Null, status);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<MachineUnreachable>(() => machine.ProbeAsync(bound.Token));
        var item = Assert.Single(status.Items, item => item.Name == "box");
        Assert.True(item.Checked);
        Assert.False(item.Reachable);
        Assert.Contains("failed to start", item.Reason);
        Assert.True(status.Items[0].Reachable);
        Assert.Null(status.Items[0].Reason);
    }

    [Fact]
    public void Successful_reads_do_not_clear_deploy_warnings_and_offline_keeps_failed_start_reason()
    {
        var status = new MachineStatus(new Topics(), ["box"]);
        status.Deployment("box", "Failed to start.");
        status.Reachable("box");
        Assert.Contains("Failed to start", status.Items[1].Reason);
        status.Unreachable("box", "didn't answer");
        Assert.False(status.Items[1].Reachable);
        Assert.Contains("Failed to start", status.Items[1].Reason);
        status.Deployment("box", null);
        status.Reachable("box");
        Assert.Null(status.Items[1].Reason);
    }
}
