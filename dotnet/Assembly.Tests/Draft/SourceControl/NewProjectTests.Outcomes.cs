using System.Text.Json;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class NewProjectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatched_init_refuses_cancel_on_its_actual_owner_then_registers_once(bool remote)
    {
        var runner = new InitializationGate();
        await using var h = await Harness.Start(localGit: remote ? null : runner, remoteGit: remote ? runner : null);
        using var client = await h.Open(); var id = Guid.NewGuid(); await h.Subscribe(client, id);
        var args = new GitOperationStart(h.LocalOperations.InstanceId, id, null, "createProject", JsonSerializer.SerializeToElement(
            new NewProjectCreate(remote ? "workbox-test" : "local", remote ? h.RemoteParent : h.LocalParent, "initialized", "git", null), ProjectsJson.Default.NewProjectCreate));
        try
        {
            await h.Call(client, 1, "start", args);
            var token = await runner.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var answer = await h.Call(client, 2, "cancel", new GitOperationArgs(id, Action: "createProject", InstanceId: h.LocalOperations.InstanceId));
            Assert.Equal("failed", answer.GetProperty("code").GetString());
            Assert.Equal("Repository initialization is already running. It will finish or reach its safety deadline.", answer.GetProperty("message").GetString());
            Assert.False(token.IsCancellationRequested);
            runner.Release.TrySetResult();
            Assert.Equal("succeeded", (await h.Terminal(client)).GetProperty("state").GetString());
            Assert.Equal(1, runner.Initializations); Assert.Equal(1, h.Hosts.Mac.Fixture.Count("projects")); Assert.Equal(1, h.Store.Revision(default));
        }
        finally { runner.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detaching_the_dialog_preserves_backend_initialization_and_committed_project_push(bool remote)
    {
        var runner = new InitializationGate();
        await using var h = await Harness.Start(localGit: remote ? null : runner, remoteGit: remote ? runner : null);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var revision = h.Hosts.Mac.Topics.Subscribe("projects", "revision", _ => { }, (_, _) => registered.TrySetResult());
        using var client = await h.Open(); var id = Guid.NewGuid(); await h.Subscribe(client, id);
        try
        {
            await h.Call(client, 1, "start", new GitOperationStart(h.LocalOperations.InstanceId, id, null, "createProject", JsonSerializer.SerializeToElement(
                new NewProjectCreate(remote ? "workbox-test" : "local", remote ? h.RemoteParent : h.LocalParent, "detached", "git", null), ProjectsJson.Default.NewProjectCreate)));
            var token = await runner.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(15));
            client.Dispose(); Assert.False(token.IsCancellationRequested);
            runner.Release.TrySetResult(); await registered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, h.Hosts.Mac.Fixture.Count("projects")); Assert.Equal(1, h.Store.Revision(default)); Assert.Equal(1, runner.Initializations);
        }
        finally { runner.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Registry_failure_rolls_back_project_and_revision_but_preserves_the_prepared_folder(bool remote, bool restored)
    {
        await using var h = await Harness.Start();
        var parent = remote ? h.RemoteParent : h.LocalParent;
        ProjectRecord? prior = null;
        if (restored) prior = SeedRemoved(h, remote ? "workbox-test" : "local", Path.Combine(parent, "remaining"));
        var revision = h.Store.Revision(default);
        var mutation = restored ? "UPDATE OF removed_at" : "INSERT";
        h.Hosts.Mac.Fixture.Db.Writer.Write("test.registration-fault", scope => scope.Execute(
            "CREATE TRIGGER reject_created_project BEFORE " + mutation + " ON projects BEGIN SELECT RAISE(ABORT,'test registration fault'); END;"));
        using var client = await h.Open(); var id = Guid.NewGuid(); await h.Subscribe(client, id);
        await h.Call(client, 1, "start", new GitOperationStart(h.LocalOperations.InstanceId, id, null, "createProject", JsonSerializer.SerializeToElement(
            new NewProjectCreate(remote ? "workbox-test" : "local", parent, "remaining", "empty", null), ProjectsJson.Default.NewProjectCreate)));
        var terminal = await h.Terminal(client);
        Assert.Equal("failed", terminal.GetProperty("state").GetString()); Assert.Contains("was not added", terminal.GetProperty("error").GetString());
        Assert.True(Directory.Exists(Path.Combine(parent, "remaining")));
        Assert.Equal(0, h.Hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM projects WHERE removed_at IS NULL"));
        Assert.Equal(revision, h.Store.Revision(default));
        if (prior is not null) Assert.Equal(prior, h.Store.Get(prior.Id));
    }

    [Fact]
    public async Task Lost_remote_start_reply_never_restarts_preparation_or_registers_an_unconfirmed_result()
    {
        var runner = new InitializationGate();
        await using var h = await Harness.Start(remoteGit: runner);
        var id = Guid.NewGuid();
        var remoteDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dropped = 0;
        using var observed = h.Hosts.Remote.Topics.Subscribe("gitOperation", h.RemoteOperations.Key(id), _ => { }, (_, json) =>
        {
            var status = JsonSerializer.Deserialize(json, GitOperationsJson.Default.GitOperationStatus)!;
            if (status.State == "accepted" && Interlocked.Exchange(ref dropped, 1) == 0) h.Hosts.Remote.Endpoint.DropAll();
            if (status.State == "succeeded") remoteDone.TrySetResult();
        });
        using var client = await h.Open(); await h.Subscribe(client, id);
        try
        {
            await h.Call(client, 1, "start", new GitOperationStart(h.LocalOperations.InstanceId, id, null, "createProject", JsonSerializer.SerializeToElement(
                new NewProjectCreate("workbox-test", h.RemoteParent, "unconfirmed", "git", null), ProjectsJson.Default.NewProjectCreate)));
            await runner.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("uncertain", (await h.Terminal(client)).GetProperty("state").GetString());
            Assert.Equal(0, h.Hosts.Mac.Fixture.Count("projects"));
            runner.Release.TrySetResult(); await remoteDone.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, runner.Initializations); Assert.True(Directory.Exists(Path.Combine(h.RemoteParent, "unconfirmed", ".git")));
            Assert.Equal(0, h.Hosts.Mac.Fixture.Count("projects")); Assert.Equal(0, h.Store.Revision(default));
        }
        finally { runner.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_project_revision_observer_failure_after_commit_does_not_report_that_registration_failed(bool restored)
    {
        await using var h = await Harness.Start();
        var prior = restored ? SeedRemoved(h, "local", Path.Combine(h.LocalParent, "committed")) : null;
        var revision = h.Store.Revision(default);
        using var fault = h.Hosts.Mac.Topics.Subscribe("projects", "revision", _ => { }, (_, _) => throw new IOException("test after SQL commit"));
        using var client = await h.Open(); var id = Guid.NewGuid(); await h.Subscribe(client, id);
        await h.Call(client, 1, "start", new GitOperationStart(h.LocalOperations.InstanceId, id, null, "createProject", JsonSerializer.SerializeToElement(
            new NewProjectCreate("local", h.LocalParent, "committed", "empty", null), ProjectsJson.Default.NewProjectCreate)));
        var terminal = await h.Terminal(client);
        Assert.Equal(1, h.Hosts.Mac.Fixture.Count("projects")); Assert.Equal(revision + 1, h.Store.Revision(default));
        Assert.Equal("succeeded", terminal.GetProperty("state").GetString());
        Assert.Equal(restored, terminal.GetProperty("result").GetProperty("alreadyExisted").GetBoolean());
        Assert.Equal(Path.Combine(h.LocalParent, "committed"), terminal.GetProperty("result").GetProperty("project").GetProperty("rootPath").GetString());
        if (prior is not null) Assert.Equal(prior.Id, terminal.GetProperty("result").GetProperty("project").GetProperty("id").GetString());
    }

    private static ProjectRecord SeedRemoved(Harness h, string machine, string path) =>
        h.Hosts.Mac.Fixture.Db.Writer.Write("test.removed-project", scope =>
        {
            var row = h.Store.AddOrRestore(scope, machine, path, "preserved title", null).Project;
            return h.Store.Remove(scope, row.Id, false);
        });

    private sealed class InitializationGate : IGit
    {
        private readonly GitCli _git = new();
        internal readonly TaskCompletionSource<CancellationToken> Dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Initializations;
        public async Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, string? stdin, TimeSpan? limit, CancellationToken ct)
        {
            if (arguments[0] == "init")
            {
                Interlocked.Increment(ref Initializations); Dispatched.TrySetResult(ct);
                await Release.Task.WaitAsync(ct);
            }
            return await _git.RunAsync(folder, arguments, stdin, limit, ct);
        }
        public IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyList<string>? input, CancellationToken ct) =>
            _git.ReadAsync(folder, arguments, input, ct);
    }
}
