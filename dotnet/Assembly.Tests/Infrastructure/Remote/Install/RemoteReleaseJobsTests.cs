using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Remote.Install;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class RemoteReleaseJobsTests : IAsyncLifetime
{
    private readonly TempDatabase _file = new();
    private readonly BackgroundWork _work = new(TimeProvider.System, TextWriter.Null);
    private readonly TopicSet _topics = new();
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
        new(new RemoteReleaseInstall(new RemoteReleaseFeed(new HttpClient(handler)), new RemoteReleaseShell(), TimeProvider.System), _topics,
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
