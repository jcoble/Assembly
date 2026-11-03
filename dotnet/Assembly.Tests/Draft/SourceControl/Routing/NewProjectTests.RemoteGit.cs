using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class NewProjectTests
{
    [Fact]
    public async Task A_remote_fetch_pushes_its_actual_operation_without_a_desktop_execution_copy()
    {
        await using var h = await Harness.Start();
        var source = await h.CloneSource();
        var git = new GitCli();
        await new GitWriter(git).InitAsync(h.RemoteParent, default);
        Assert.Equal(0, (await git.RunAsync(h.RemoteParent, ["remote", "add", "origin", source], null, null, default)).ExitCode);
        var owned = h.Hosts.Remote.Sessions.Create("fixture", "", workingFolder: h.RemoteParent);
        h.Hosts.Mac.Sessions.Add(owned.SessionId, "fixture", "", "workbox-test", workingFolder: h.LocalParent);
        using var client = await h.Open();
        var host = (await h.Call(client, 1, "operation_host", new { session = owned.SessionId, action = "fetch" })).GetProperty("result");
        Assert.Equal(h.Hosts.Remote.Fixture.Machine, host.GetProperty("machine").GetString());
        var id = Guid.NewGuid();
        await h.Subscribe(client, id, h.Hosts.Remote.Fixture.Machine);
        var request = new GitOperationStart(host.GetProperty("instanceId").GetString()!, id, owned.SessionId, "fetch",
            System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone());
        Assert.Equal("result", (await h.Call(client, 2, "start", request)).GetProperty("t").GetString());
        var terminal = await h.Terminal(client);
        Assert.Equal("succeeded", terminal.GetProperty("state").GetString());
        Assert.Equal(id, terminal.GetProperty("operationId").GetGuid());
        Assert.Equal(h.Hosts.Remote.Fixture.Machine, terminal.GetProperty("machine").GetString());
        Assert.Equal("operation_missing", h.LocalOperations.Status(id).Error);
        Assert.NotEqual("", (await git.RunAsync(h.RemoteParent, ["for-each-ref", "refs/remotes/origin"], null, null, default)).Output);
    }

    [Fact]
    public async Task Remote_cancel_stops_only_the_original_execution_and_unsubscribe_releases_its_owner_watch()
    {
        var git = new BlockedFetch();
        await using var h = await Harness.Start(remoteGit: git);
        await new GitWriter(new GitCli()).InitAsync(h.RemoteParent, default);
        var owned = h.Hosts.Remote.Sessions.Create("fixture", "", workingFolder: h.RemoteParent);
        h.Hosts.Mac.Sessions.Add(owned.SessionId, "fixture", "", "workbox-test", workingFolder: h.LocalParent);
        using var client = await h.Open();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var host = (await h.Call(client, 1, "operation_host", new { session = owned.SessionId, action = "fetch" })).GetProperty("result");
        var instance = host.GetProperty("instanceId").GetString()!;
        var id = Guid.NewGuid();
        await h.Subscribe(client, id, h.Hosts.Remote.Fixture.Machine);
        var request = new GitOperationStart(instance, id, owned.SessionId, "fetch", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone());
        Assert.Equal("result", (await h.Call(client, 2, "start", request)).GetProperty("t").GetString());
        await git.Entered.Task.WaitAsync(deadline.Token);
        Assert.Equal("result", (await h.Call(client, 3, "cancel", new GitOperationArgs(id, owned.SessionId, "fetch", instance))).GetProperty("t").GetString());
        await git.Stopped.Task.WaitAsync(deadline.Token);
        var terminal = await h.Terminal(client);
        Assert.Equal("uncertain", terminal.GetProperty("state").GetString());
        Assert.Equal(1, git.Fetches);
        Assert.Equal("operation_missing", h.LocalOperations.Status(id).Error);
        var unsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = h.Hosts.Remote.Fixture.Machine + "/" + id.ToString("D");
        void Removed(string topic, string? removed) { if (topic == "gitOperation" && removed == key) unsubscribed.TrySetResult(); }
        h.Hosts.Remote.Topics.Unsubscribed += Removed;
        try
        {
            await h.Unsubscribe(client);
            await unsubscribed.Task.WaitAsync(deadline.Token);
            Assert.False(h.Hosts.Remote.Topics.HasSubscribers("gitOperation", key));
        }
        finally { h.Hosts.Remote.Topics.Unsubscribed -= Removed; }
    }

    [Fact]
    public async Task Losing_the_captured_remote_owner_reports_uncertainty_without_inventing_a_completion_or_version()
    {
        var git = new BlockedFetch();
        await using var h = await Harness.Start(remoteGit: git);
        await new GitWriter(new GitCli()).InitAsync(h.RemoteParent, default);
        var owned = h.Hosts.Remote.Sessions.Create("fixture", "", workingFolder: h.RemoteParent);
        h.Hosts.Mac.Sessions.Add(owned.SessionId, "fixture", "", "workbox-test", workingFolder: h.LocalParent);
        using var client = await h.Open();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var host = (await h.Call(client, 1, "operation_host", new { session = owned.SessionId, action = "fetch" })).GetProperty("result");
        var instance = host.GetProperty("instanceId").GetString()!;
        var id = Guid.NewGuid();
        await h.Subscribe(client, id, h.Hosts.Remote.Fixture.Machine);
        await h.Call(client, 2, "start", new GitOperationStart(instance, id, owned.SessionId, "fetch", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()));
        await git.Entered.Task.WaitAsync(deadline.Token);
        var actual = await h.Running(client);
        await h.Hosts.DisconnectMirror();
        var observed = await h.Terminal(client);
        Assert.Equal("uncertain", observed.GetProperty("state").GetString());
        Assert.Equal(actual.GetProperty("version").GetInt64(), observed.GetProperty("version").GetInt64());
        Assert.Equal(actual.GetProperty("instanceId").GetString(), observed.GetProperty("instanceId").GetString());
        Assert.Equal(actual.GetProperty("machine").GetString(), observed.GetProperty("machine").GetString());
        Assert.Equal(id, observed.GetProperty("operationId").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, observed.GetProperty("result").ValueKind);
        Assert.Equal("running", h.RemoteOperations.Status(id).State);
        Assert.Equal(1, git.Fetches);
        Assert.False(git.Stopped.Task.IsCompleted);
    }

    [Fact]
    public async Task A_disconnected_session_owner_refuses_writes_before_either_checkout_is_touched()
    {
        await using var h = await Harness.Start();
        var git = new GitCli();
        await new GitWriter(git).InitAsync(h.LocalParent, default);
        await new GitWriter(git).InitAsync(h.RemoteParent, default);
        await File.WriteAllTextAsync(Path.Combine(h.LocalParent, "owned.txt"), "desktop");
        await File.WriteAllTextAsync(Path.Combine(h.RemoteParent, "owned.txt"), "owner");
        var owned = h.Hosts.Remote.Sessions.Create("fixture", "", workingFolder: h.RemoteParent);
        h.Hosts.Mac.Sessions.Add(owned.SessionId, "fixture", "", "workbox-test", workingFolder: h.LocalParent);
        using var client = await h.Open();
        await h.Hosts.DisconnectMirror();
        var refused = await h.Call(client, 1, "stage", new { session = owned.SessionId, paths = new[] { "owned.txt" } });
        Assert.Equal("error", refused.GetProperty("t").GetString());
        Assert.Equal("source_changed", refused.GetProperty("code").GetString());
        Assert.Equal("", (await git.RunAsync(h.LocalParent, ["diff", "--cached", "--name-only"], null, null, default)).Output);
        Assert.Equal("", (await git.RunAsync(h.RemoteParent, ["diff", "--cached", "--name-only"], null, null, default)).Output);
    }

    [Fact]
    public async Task History_reads_require_the_original_remote_source_and_refuse_stale_or_missing_sources()
    {
        await using var h = await Harness.Start();
        var repository = await h.CloneSource();
        var owned = h.Hosts.Remote.Sessions.Create("fixture", "", workingFolder: repository);
        h.Hosts.Mac.Sessions.Add(owned.SessionId, "fixture", "", "workbox-test", workingFolder: h.LocalParent);
        var origin = h.Hosts.Mac.Fixture.Db.QueryFirst(
            "SELECT machine,generation,name FROM engine_mirror WHERE state='live' AND name='workbox-test'",
            row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default)!;
        using var client = await h.Open();
        var missing = await h.Call(client, 1, "history", new { session = owned.SessionId });
        Assert.Equal("source_changed", missing.GetProperty("code").GetString());
        var stale = await h.Call(client, 2, "history", new
        {
            session = owned.SessionId,
            expectedSource = new { machine = origin.Machine, generation = origin.Generation + 1, routeMachine = origin.RouteMachine }
        });
        Assert.Equal("source_changed", stale.GetProperty("code").GetString());
        var valid = await h.Call(client, 3, "history", new
        {
            session = owned.SessionId,
            expectedSource = new { machine = origin.Machine, generation = origin.Generation, routeMachine = origin.RouteMachine }
        });
        Assert.Equal("result", valid.GetProperty("t").GetString());
        Assert.Equal("fixture", Assert.Single(valid.GetProperty("result").GetProperty("commits").EnumerateArray()).GetProperty("subject").GetString());
        var ownerMarker = await h.Call(client, 4, "history", new
        {
            session = owned.SessionId,
            expectedSource = new { machine = origin.Machine, generation = 0, routeMachine = (string?)null }
        });
        Assert.Equal("source_changed", ownerMarker.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_local_read_refuses_a_foreign_assertion_before_reading_its_checkout()
    {
        await using var h = await Harness.Start();
        var owned = h.Hosts.Mac.Sessions.Create("fixture", "", workingFolder: h.LocalParent);
        using var client = await h.Open();
        var refused = await h.Call(client, 1, "status", new
        {
            session = owned.SessionId,
            expectedSource = new { machine = h.Hosts.Remote.Fixture.Machine, generation = 0, routeMachine = (string?)null }
        });
        Assert.Equal("source_changed", refused.GetProperty("code").GetString());
        await new GitWriter(new GitCli()).InitAsync(h.LocalParent, default);
        var accepted = await h.Call(client, 2, "status", new
        {
            session = owned.SessionId,
            expectedSource = new { machine = h.Hosts.Mac.Fixture.Machine, generation = 0, routeMachine = (string?)null }
        });
        Assert.Equal("result", accepted.GetProperty("t").GetString());
    }

    private sealed class BlockedFetch : Assembly.Application.Common.Git.IGit
    {
        private readonly GitCli _inner = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Fetches;
        public async Task<Assembly.Application.Common.Git.GitRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan? limit, CancellationToken ct)
        {
            if (args[0] != "fetch") return await _inner.RunAsync(folder, args, input, limit, ct);
            Interlocked.Increment(ref Fetches); Entered.TrySetResult();
            try { await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct); return new(0, "", ""); }
            finally { Stopped.TrySetResult(); }
        }
        public IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> args, IReadOnlyList<string>? input, CancellationToken ct) => _inner.ReadAsync(folder, args, input, ct);
    }

    [Fact]
    public async Task A_remote_session_status_and_stage_use_only_its_actual_owner_checkout()
    {
        await using var h = await Harness.Start();
        var git = new GitCli();
        await new GitWriter(git).InitAsync(h.LocalParent, default);
        await new GitWriter(git).InitAsync(h.RemoteParent, default);
        await File.WriteAllTextAsync(Path.Combine(h.LocalParent, "local.txt"), "desktop");
        await File.WriteAllTextAsync(Path.Combine(h.RemoteParent, "remote ü.txt"), "owner");
        var owned = h.Hosts.Remote.Sessions.Create("fixture", "", workingFolder: h.RemoteParent);
        h.Hosts.Mac.Sessions.Add(owned.SessionId, "fixture", "", "workbox-test", workingFolder: h.LocalParent);
        using var client = await h.Open();
        var origin = h.Hosts.Mac.Fixture.Db.QueryFirst(
            "SELECT machine,generation,name FROM engine_mirror WHERE state='live' AND name='workbox-test'",
            row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default)!;
        var missing = await h.Call(client, 10, "status", new { session = owned.SessionId });
        Assert.Equal("source_changed", missing.GetProperty("code").GetString());
        var before = await h.Call(client, 1, "status", new
        {
            session = owned.SessionId,
            expectedSource = new { machine = origin.Machine, generation = origin.Generation, routeMachine = origin.RouteMachine }
        });
        Assert.Equal("result", before.GetProperty("t").GetString());
        Assert.Contains(before.GetProperty("result").GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == "remote ü.txt");
        Assert.DoesNotContain(before.GetProperty("result").GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == "local.txt");
        var staged = await h.Call(client, 2, "stage", new { session = owned.SessionId, paths = new[] { "remote ü.txt" } });
        Assert.Equal("result", staged.GetProperty("t").GetString());
        Assert.Equal("remote ü.txt", (await git.RunAsync(h.RemoteParent, ["diff", "--cached", "--name-only", "-z"], null, null, default)).Output.TrimEnd('\0'));
        Assert.Equal("", (await git.RunAsync(h.LocalParent, ["diff", "--cached", "--name-only"], null, null, default)).Output);
        var overrideOwner = await h.Call(client, 3, "operation_host", new { session = owned.SessionId, action = "push", machine = h.Hosts.Remote.Fixture.Machine });
        Assert.Equal("failed", overrideOwner.GetProperty("code").GetString());
    }
}
