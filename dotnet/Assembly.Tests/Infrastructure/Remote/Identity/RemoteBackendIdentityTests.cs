using System.Text.Json;
using Assembly.Application.Common.Calls;
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Mismatch_keeps_remote_readable_but_refuses_new_sessions_before_send(bool bulk, bool bulkOnly)
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
            BackendIdentity = bulkOnly ? BackendBuild.Identity : new string('0', 64),
            BulkBackendIdentity = bulkOnly ? new string('0', 64) : null,
            Reply = (_, method) => { calls.Add(method); return "{}"; }
        };
        await remote.StartAsync();
        await using var machine = new RemoteMachine("box", remote.Address, "box-token", hub, sessions, TextWriter.Null, status);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await machine.ProbeAsync(ct.Token);
        using var args = JsonDocument.Parse("{}");
        await machine.CallAsync("sessions", "byClient", args.RootElement, bulk, ct.Token);
        Assert.Equal("backend_mismatch", Assert.Throws<CallFailed>(() => machine.RequireMatchingBackendForStart()).Reason);
        var item = Assert.Single(status.Items, item => item.Name == "box");
        Assert.True(item.Reachable);
        Assert.Contains(remote.Commit, item.Reason);
        Assert.Contains(BackendBuild.Commit!, item.Reason);
        foreach (var (feature, method, json) in new[] {
            ("sessions", "create", "{}"), ("sessions", "create_at_root", "{}"),
            ("engine", "machine_command", """{"command":{"kind":"start_session"}}"""),
            ("engine", "start_title_begin", "{}"), ("engine", "start_title_commit", "{}") })
        {
            using var start = JsonDocument.Parse(json);
            var failure = await Assert.ThrowsAsync<CallFailed>(() => machine.CallAsync(feature, method, start.RootElement, bulk, ct.Token));
            Assert.Equal("backend_mismatch", failure.Reason);
            Assert.DoesNotContain(method, calls);
        }
        await machine.CallAsync("sessions", "byClient", args.RootElement, bulk, ct.Token);
        Assert.Equal(2, calls.Count(method => method == "byClient"));
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
    public void Same_inputs_at_a_different_revision_match_and_missing_identity_is_refused()
    {
        Assert.Null(BackendBuild.Mismatch(BackendBuild.Identity, new string('b', 40)));
        Assert.NotNull(BackendBuild.Mismatch(null, BackendBuild.Commit));
    }

    [Fact]
    public void Successful_reads_do_not_clear_version_or_deploy_warnings_and_offline_keeps_failed_start_reason()
    {
        var status = new MachineStatus(new Topics(), ["box"]);
        status.BackendMismatch("box", "Remote is on an older backend.");
        status.Deployment("box", "Failed to start.");
        status.Reachable("box");
        Assert.Contains("older backend", status.Items[1].Reason);
        Assert.Contains("Failed to start", status.Items[1].Reason);
        status.Unreachable("box", "didn't answer");
        Assert.False(status.Items[1].Reachable);
        Assert.Contains("Failed to start", status.Items[1].Reason);
        status.BackendMismatch("box", null);
        status.Deployment("box", null);
        status.Reachable("box");
        Assert.Null(status.Items[1].Reason);
    }
}
