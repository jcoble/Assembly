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
        await _box.DisposeAsync();
        await _stub.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _file.Dispose();
    }

    [Fact]
    public async Task A_watch_while_the_session_is_followed_follows_it_once_more_after()
    {
        _stub.Pings = false;
        _box.WatchRunning(["s1"], []);
        await Until(() => _stub.LiveHits == 1);

        _box.WatchRunning(["s1"], []);

        // The first link settles after 2 s of quiet; the second watch is still owed a pass.
        await Until(() => _stub.LiveHits == 2);
    }

    [Fact]
    public async Task Watches_at_the_same_moment_start_one_follower()
    {
        // Pings keep every link from settling, so no owed second pass opens while the test counts.
        _stub.Pings = true;
        var ids = Enumerable.Range(0, 40).Select(n => $"s{n}").ToArray();
        foreach (var id in ids)
        {
            using var ready = new Barrier(8);
            var watches = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                ready.SignalAndWait();
                _box.WatchRunning([id], []);
            })).ToArray();
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

        await Until(() => _stub.LiveHits == 1);
    }

    [Fact]
    public async Task A_follower_that_gives_up_with_a_run_open_does_not_try_again_on_its_own()
    {
        _hub.PublishCopy(new StoredRow("s1", 1, "r1", null, "turn", JsonDocument.Parse("""{"state":"started"}""").RootElement, 1, PrevSeq: 0));
        Assert.NotNull(_hub.OpenRun("s1"));
        _stub.DropVersion = true;

        _box.WatchRunning(["s1"], []);
        await Until(() => _stub.VersionHits == 1);
        await Task.Delay(1500);

        Assert.Equal(1, _stub.VersionHits);
        Assert.Equal(0, _stub.LiveHits);
    }

    private static async Task Until(Func<bool> done)
    {
        for (var waited = TimeSpan.Zero; !done(); waited += TimeSpan.FromMilliseconds(10))
        {
            Assert.True(waited < Wait, "it didn't happen within 10 s");
            await Task.Delay(10);
        }
    }

    // A log that, once held, stops the first line it's given until it's released.
    private sealed class HeldLog : StringWriter
    {
        private readonly ManualResetEventSlim _open = new(true);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold() => _open.Reset();

        public void Release() => _open.Set();

        public override void WriteLine(string? value)
        {
            Reached.TrySetResult();
            _open.Wait();
            base.WriteLine(value);
        }
    }
}
