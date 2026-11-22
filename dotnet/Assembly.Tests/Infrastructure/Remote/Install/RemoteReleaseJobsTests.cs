using System.Net;
using System.Text;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Desktop.Settings;
using Assembly.Engine.Persistence;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Topics;
using Assembly.Infrastructure.Updates;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class RemoteReleaseJobsTests : IAsyncLifetime
{
    private readonly TempDatabase _file = new();
    private readonly BackgroundWork _work = new(TimeProvider.System, TextWriter.Null);
    private Assembly.Infrastructure.Persistence.AppDatabase _db = null!;

    public Task InitializeAsync() { _db = TestDatabase.Open(_file.Path); return Task.CompletedTask; }
    public async Task DisposeAsync() { await _work.DisposeAsync(); _db.Dispose(); _file.Dispose(); }

    private sealed class Gate : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Open.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }

    private RemoteReleaseJobs Jobs(HttpMessageHandler handler, List<string>? connected = null) =>
        new(new RemoteReleaseInstall(new RemoteReleaseFeed(new HttpClient(handler)), new RemoteReleaseShell(), TimeProvider.System),
            (name, _) => name is "box" or "second" ? new SavedMachine(name, name + "-host") : null,
            (name, _) => { connected?.Add(name); return Task.CompletedTask; });

    [Fact]
    public async Task A_failed_check_ends_the_install_with_its_message_and_never_connects()
    {
        var connected = new List<string>();
        var gate = new Gate();
        gate.Open.SetResult();
        await using var jobs = Jobs(gate, connected);
        var failed = await Assert.ThrowsAsync<CallFailed>(() => jobs.InstallAsync("box", _ => { }, CancellationToken.None));
        Assert.Equal("release_unavailable", failed.Reason);
        Assert.Contains("HTTP 404", failed.Message, StringComparison.Ordinal);
        Assert.Empty(connected);
    }

    private sealed class NpmVersions : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var name = url[1..^"/latest".Length];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent($"{{\"name\":\"{name}\",\"version\":\"2.0.0\"}}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixtureShell : RemoteReleaseShell
    {
        internal override Task<string> RunAsync(string target, string command, Action<string>? line, CancellationToken ct) =>
            Task.FromResult(
                command.Contains("uname -s", StringComparison.Ordinal) ? "Linux x86_64"
                : command.Contains("folder=fresh", StringComparison.Ordinal) ? "ours\tours\tfree"
                : command.Contains("\"$HOME\"", StringComparison.Ordinal) ? "/home/fixture"
                : "");
    }

    private sealed class ToolFailingPackages : IRemotePackages, IRemotePackage
    {
        public Task<IRemotePackage> PrepareAsync(CancellationToken ct) => Task.FromResult<IRemotePackage>(this);
        public Task<ReceivedPackage> ReceiveAsync(string target, string stage, Action<string> phase, CancellationToken ct) =>
            Task.FromResult(new ReceivedPackage("1.0.0", "fixture", new string('a', 64)));
        public Task<AdapterArguments> InstallAdaptersAsync(string target, string quotedStage, bool keep, Action<string> phase, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AdapterArguments?> UpdateToolsAsync(string target, string quotedStage, ToolsBehind tools, Action<string> phase, CancellationToken ct) =>
            throw new CallFailed("codex: raw npm boom", "remote_command_failed", "cli:codex");
    }

    // The whole attribution chain: a remote tools update fails on Codex's CLI, the tool survives
    // the job record and the update aggregation, and the composed state blames Codex on that
    // machine — with the machine named — and no other provider.
    [Fact]
    public async Task A_remote_update_failing_on_a_providers_tool_is_attributed_to_that_provider()
    {
        var topics = new Topics();
        var status = new MachineStatus(topics, ["box"]);
        status.Reachable("box");
        status.Updates("box", [
            new UpdateItem("cli", "codex", "1.0.0", "2.0.0", UpdateItem.Behind),
            new UpdateItem("cli", "claude", "1.0.0", "2.0.0", UpdateItem.Behind)]);

        var install = new RemoteReleaseInstall(
            new RemoteReleaseFeed(new HttpClient(new Gate())), new FixtureShell(), TimeProvider.System,
            version: null, packages: new ToolFailingPackages());
        await using var jobs = new RemoteReleaseJobs(install,
            (name, _) => name is "box" ? new SavedMachine(name, name + "-host") : null,
            (_, _) => Task.CompletedTask);
        var source = new ReleaseUpdateSource(new CliReleaseSource(new HttpClient(new NpmVersions()), TimeProvider.System), jobs, TimeProvider.System);
        var updates = new MachineUpdates(source, status,
            new InstalledCalls(new AgentsOnlyLauncher(() => []), TimeProvider.System),
            (_, _) => Task.FromResult<InstalledTools?>(new InstalledTools(
                new Dictionary<string, string> { ["claude-agent-acp"] = "2.0.0", ["codex-acp"] = "2.0.0" }, null,
                [new InstalledCli("codex", "1.0.0"), new InstalledCli("claude", "1.0.0")])),
            null, _work, TextWriter.Null);

        Assert.True(updates.StartMachine("box"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (updates.FailureOf("box") is null || status.Items.Single(item => item.Name == "box").Updating)
            await Task.Delay(10, deadline.Token);

        var recorded = updates.FailureOf("box");
        Assert.NotNull(recorded);
        Assert.Equal("update", recorded.Operation);
        Assert.Equal("cli", recorded.Kind);
        Assert.Equal("codex", recorded.Name);
        Assert.DoesNotContain("raw npm boom", status.Items.Single(item => item.Name == "box").UpdateNote!, StringComparison.Ordinal);

        var engineMachine = EngineSchema.Open(_db, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        var sessions = SessionStore.Open(_db, TimeProvider.System, _file.Folder);
        var machinesFeature = new MachinesFeature(new MachineSet(new FakeMachine(null, sessions)), status, () => { }, TimeProvider.System);
        var launcher = new FakeLauncher
        {
            Agents =
            [
                new("claude", "Claude Code", "claude-agent-acp", [], null, VendorFamily: "claude"),
                new("codex", "Codex", "codex-acp", [], null, VendorFamily: "codex"),
            ]
        };
        var enablement = new ProviderEnablementStore(_db, TimeProvider.System);
        enablement.SetEnabled("claude", true);
        enablement.SetEnabled("codex", true);
        var composer = new SettingsStateComposer(_db, launcher, machinesFeature, engineMachine, TimeProvider.System,
            updateFailureOf: updates.FailureOf);
        try
        {
            var state = composer.Compose(CancellationToken.None);
            var codexOnBox = state.Providers.Single(p => p.Id == "codex").Machines.Single(m => m.MachineKey == "box");
            Assert.Equal(ProviderRowState.Problem, codexOnBox.State);
            Assert.NotNull(codexOnBox.LastFailure);
            Assert.Equal(SettingsFailure.RetryInstall, codexOnBox.LastFailure.Retry);
            Assert.Equal("update", codexOnBox.LastFailure.Operation);
            Assert.Equal("box", codexOnBox.LastFailure.MachineKey);
            Assert.Equal("cli:codex", codexOnBox.LastFailure.Tool);
            var claudeOnBox = state.Providers.Single(p => p.Id == "claude").Machines.Single(m => m.MachineKey == "box");
            Assert.Null(claudeOnBox.LastFailure);
        }
        finally { await machinesFeature.DisposeAsync(); }
    }

    [Fact]
    public async Task The_one_update_goes_through_the_same_job_and_gets_its_failure_and_progress()
    {
        var gate = new Gate();
        gate.Open.SetResult();
        await using var jobs = Jobs(gate);
        var phases = new List<string>();
        var failed = await Assert.ThrowsAsync<CallFailed>(() => jobs.InstallAsync("box", phases.Add, CancellationToken.None));
        Assert.Equal("release_unavailable", failed.Reason);
        Assert.NotEmpty(phases);
        await Assert.ThrowsAsync<CallFailed>(() => jobs.InstallAsync("nobody", _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task The_one_update_waits_its_turn_behind_a_running_install()
    {
        var gate = new Gate();
        await using var jobs = Jobs(gate);
        var running = jobs.InstallAsync("box", _ => { }, CancellationToken.None);
        var busy = await Assert.ThrowsAsync<CallFailed>(() => jobs.InstallAsync("box", _ => { }, CancellationToken.None));
        Assert.Equal("install_busy", busy.Reason);
        gate.Open.SetResult();
        await Assert.ThrowsAsync<CallFailed>(() => running);
    }

    [Fact]
    public async Task A_machine_waiting_for_its_turn_holds_up_no_other_machine()
    {
        var gate = new Gate();
        await using var jobs = Jobs(gate);
        var (box, second) = (jobs.InstallAsync("box", _ => { }, CancellationToken.None), jobs.InstallAsync("second", _ => { }, CancellationToken.None));
        gate.Open.SetResult();
        Assert.Equal("release_unavailable", (await Assert.ThrowsAsync<CallFailed>(() => box)).Reason);
        Assert.Equal("release_unavailable", (await Assert.ThrowsAsync<CallFailed>(() => second)).Reason);
    }

    [Fact]
    public async Task Cancelling_an_install_stops_it_and_frees_the_machine()
    {
        var gate = new Gate();
        await using var jobs = Jobs(gate);
        using var cancel = new CancellationTokenSource();
        var running = jobs.InstallAsync("box", _ => { }, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        gate.Open.SetResult();
        await Assert.ThrowsAsync<CallFailed>(() => jobs.InstallAsync("box", _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_machine_is_refused()
    {
        await using var jobs = Jobs(new Gate());
        Assert.Equal("unknown_machine", (await Assert.ThrowsAsync<CallFailed>(() => jobs.InstallAsync("other", _ => { }, CancellationToken.None))).Reason);
    }
}
