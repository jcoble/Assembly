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
        new(new RemoteReleaseInstall(new RemoteReleaseFeed(new HttpClient(handler)), new RemoteReleaseShell(), TimeProvider.System), _work, _topics,
            (name, _) => name == "box" ? new SavedMachine("box", "box-host") : null,
            (name, _) => { connected?.Add(name); return Task.CompletedTask; });

    private static async Task<RemoteReleaseJob> Settled(RemoteReleaseJobs jobs, Guid id)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (jobs.TryStatus(id) is not { State: not "running" } job)
        {
            await Task.Delay(5, limit.Token);
            continue;
        }
        return jobs.TryStatus(id)!;
    }

    [Fact]
    public async Task A_failed_check_ends_the_job_with_its_message_and_never_connects()
    {
        var connected = new List<string>();
        var gate = new Gate();
        gate.Open.SetResult();
        await using var jobs = Jobs(gate, connected);
        var id = Guid.NewGuid();
        _db.Writer.Write("test.release", scope => jobs.Admit(new(id, "box"), scope));
        var job = await Settled(jobs, id);
        Assert.Equal("failed", job.State);
        Assert.Equal("release_unavailable", job.ErrorCode);
        Assert.Contains("HTTP 404", job.Message, StringComparison.Ordinal);
        Assert.Empty(connected);
    }

    [Fact]
    public async Task Only_one_install_runs_and_cancelling_it_stops_it()
    {
        await using var jobs = Jobs(new Gate());
        var id = Guid.NewGuid();
        _db.Writer.Write("test.release", scope => jobs.Admit(new(id, "box"), scope));
        var busy = Assert.Throws<CallFailed>(() => _db.Writer.Write("test.release", scope => jobs.Admit(new(Guid.NewGuid(), "box"), scope)));
        Assert.Equal("install_busy", busy.Reason);
        jobs.Cancel(id);
        Assert.Equal("cancelled", (await Settled(jobs, id)).State);
    }

    [Fact]
    public async Task An_unknown_machine_is_refused_and_a_rolled_back_write_starts_nothing()
    {
        await using var jobs = Jobs(new Gate());
        var unknown = Assert.Throws<CallFailed>(() => _db.Writer.Write("test.release", scope => jobs.Admit(new(Guid.NewGuid(), "other"), scope)));
        Assert.Equal("unknown_machine", unknown.Reason);
        var id = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => _db.Writer.Write<RemoteReleaseJob>("test.release", scope =>
        { jobs.Admit(new(id, "box"), scope); throw new InvalidOperationException("rollback"); }));
        Assert.Null(jobs.TryStatus(id));
    }
}
