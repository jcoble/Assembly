using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>
/// The remote follower's watches, against the machines tests' stub remote: one follower per session, and a watch that
/// comes while a follower stops or gives up still leaves one running.
/// </summary>
public sealed class RemoteFollowerTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _file = new();
    private readonly MachinesSocketTests.StubRemote _stub = new();
    private readonly HeldLog _log = new();
    private AppDatabase _db = null!;
    private LiveHub _hub = null!;
    private RemoteMachine _box = null!;
    private bool _boxStopped;

    public async Task InitializeAsync()
    {
        await _stub.StartAsync();
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _hub = new LiveHub(RowStore.Open(_db), topics: topics);
        _box = new RemoteMachine("box", _stub.Address, "box-token", _hub, SessionStore.Open(_db, TimeProvider.System, "checkouts"), _log,
            new MachineStatus(topics, ["box"]));
    }

    public async Task DisposeAsync()
    {
        _log.Release();
        await StopBoxAsync();
        await _stub.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _file.Dispose();
    }

    [Fact]
    public async Task A_watch_while_the_session_is_followed_follows_it_once_more_after()
    {
        _box.WatchRunning(["s1"], []);
        await _stub.LiveHits.ReachedAsync(1);

        _box.WatchRunning(["s1"], []);

        // The first link settles after 2 s of quiet; the second watch is still owed a pass.
        await _stub.LiveHits.ReachedAsync(2);
    }

    [Fact]
    public async Task Watches_at_the_same_moment_start_one_follower()
    {
        var ids = Enumerable.Range(0, 40).Select(n => $"s{n}").ToArray();
        // An open run keeps every link from settling, so no owed second pass opens while the test counts.
        foreach (var id in ids)
            _hub.PublishCopy(new StoredRow(id, 1, "r1", null, "turn", JsonDocument.Parse("""{"state":"started"}""").RootElement, 1, PrevSeq: 0));
        foreach (var id in ids)
        {
            using var ready = new Barrier(8);
            // Own threads, so the eight blocked at the barrier don't starve the pool the follower runs on.
            var watches = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
            {
                ready.SignalAndWait();
                _box.WatchRunning([id], []);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            await Task.WhenAll(watches);
        }

        // One link per session at once means one follower; eight followers would show here as eight links.
        await Task.WhenAll(ids.Select(_stub.LiveSeenAsync)).WaitAsync(Wait);
        Assert.Equal(1, _stub.MostLiveAtOnce);
    }

    [Fact]
    public async Task A_watch_while_the_follower_gives_up_starts_a_new_follow()
    {
        _stub.DropVersion = true;
        _log.Hold();
        _box.WatchRunning(["s1"], []);
        // Held in the give-up, where it logs why.
        await _log.Reached.Task.WaitAsync(Wait);

        _box.WatchRunning(["s1"], []);
        _stub.DropVersion = false;
        _log.Release();

        await _stub.LiveHits.ReachedAsync(1);
    }

    [Fact]
    public async Task A_follower_that_gives_up_with_a_run_open_does_not_try_again_on_its_own()
    {
        _hub.PublishCopy(new StoredRow("s1", 1, "r1", null, "turn", JsonDocument.Parse("""{"state":"started"}""").RootElement, 1, PrevSeq: 0));
        Assert.NotNull(_hub.OpenRun("s1"));
        _stub.DropVersion = true;

        _box.WatchRunning(["s1"], []);
        // It logs why as it gives up.
        await _log.GaveUp.Task.WaitAsync(Wait);

        // No try in the backoff window (1 s) and some margin, while the follower still runs.
        await Assert.ThrowsAsync<TimeoutException>(() => _stub.VersionHits.ReachedAsync(2).WaitAsync(TimeSpan.FromSeconds(1.5)));
        await StopBoxAsync();

        Assert.Equal(1, _stub.VersionHits.Value);
        Assert.Equal(0, _stub.LiveHits.Value);
    }

    // A machine is stopped once.
    private async Task StopBoxAsync()
    {
        if (!_boxStopped)
            await _box.DisposeAsync();
        _boxStopped = true;
    }

    // A log that, once held, stops the first line it's given until it's released.
    private sealed class HeldLog : StringWriter
    {
        private readonly ManualResetEventSlim _open = new(true);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The line the follower writes as it gives up on an unreachable remote.
        public TaskCompletionSource GaveUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold() => _open.Reset();

        public void Release() => _open.Set();

        public override void WriteLine(string? value)
        {
            Reached.TrySetResult();
            if (value?.Contains("didn't answer") == true)
                GaveUp.TrySetResult();
            _open.Wait();
            base.WriteLine(value);
        }
    }
}
