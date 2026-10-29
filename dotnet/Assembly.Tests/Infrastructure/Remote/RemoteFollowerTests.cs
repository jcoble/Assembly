using System.Net;
using System.Net.WebSockets;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>
/// A followed session's link, quiet on a remote that sends nothing, on fake time: the follower waits for a reason to look
/// again (its last viewer leaving, a frame) instead of waking on a timer while someone watches.
/// </summary>
public sealed class RemoteFollowerTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PastSettled = TimeSpan.FromSeconds(3);

    private readonly TempDatabase _db = new();
    private readonly AppDatabase _database;
    private readonly TopicSet _topics = new();
    private readonly LiveHub _hub;
    private readonly CountingTime _time = new(DateTimeOffset.UtcNow);
    private readonly Assembly.Tests.Draft.Base.MachinesSocketTests.StubRemote _remote = new();
    private RemoteMachine? _machine;

    public RemoteFollowerTests()
    {
        _database = TestDatabase.Open(_db.Path);
        _hub = new LiveHub(RowStore.Open(_database), topics: _topics);
    }

    [Fact]
    public async Task A_viewed_session_s_follower_makes_no_timers_while_its_link_is_quiet()
    {
        using var viewer = _hub.Subscribe("s1", 0);
        await FollowPastSettledAsync();

        var made = _time.Made;
        for (var second = 0; second < 60; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(made, _time.Made);
        Assert.True(_remote.LiveOpen > 0);
    }

    [Fact]
    public async Task A_follower_settles_as_soon_as_its_last_watcher_leaves()
    {
        var viewer = _hub.Subscribe("s1", 0);
        await FollowPastSettledAsync();

        viewer.Dispose();

        await _remote.NoLiveAsync().WaitAsync(Wait);
    }

    // The window views sessions through the rows topic, not /live: the same, for a rows-topic viewer.
    [Fact]
    public async Task A_rows_topic_viewer_s_follower_makes_no_timers_while_its_link_is_quiet()
    {
        var viewer = _topics.Subscribe(RowsFeature.Topic, "s1", _ => { }, (_, _) => { });
        await FollowPastSettledAsync();

        var made = _time.Made;
        for (var second = 0; second < 60; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(made, _time.Made);
        Assert.True(_remote.LiveOpen > 0);
        _topics.Unsubscribe(viewer);
    }

    [Fact]
    public async Task A_follower_settles_as_soon_as_its_last_rows_topic_viewer_unsubscribes()
    {
        var viewer = _topics.Subscribe(RowsFeature.Topic, "s1", _ => { }, (_, _) => { });
        await FollowPastSettledAsync();
        Assert.True(_remote.LiveOpen > 0);

        _topics.Unsubscribe(viewer);

        await _remote.NoLiveAsync().WaitAsync(Wait);
    }

    // The follower runs over the shared machine socket, not a per-session /live client.
    private async Task FollowPastSettledAsync()
    {
        await _remote.StartAsync();
        _machine = new RemoteMachine("box", _remote.Address, "box-token", _hub,
            SessionStore.Open(_database, TimeProvider.System, "checkouts"), new SharedLog(),
            new MachineStatus(_topics, ["box"]), time: _time);
        _machine.WatchRunning(["s1"], []);
        await _remote.LiveSeenAsync("s1").WaitAsync(Wait);
        _time.Advance(PastSettled);
    }

    public async ValueTask DisposeAsync()
    {
        if (_machine is not null)
            await _machine.DisposeAsync();
        await _remote.DisposeAsync();
        _hub.Dispose();
        _database.Dispose();
        _db.Dispose();
    }

    // Counts the timers made, which is what a wake costs.
    private sealed class CountingTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _made;
        public int Made => Volatile.Read(ref _made);

        // Done once the follower's settle waits out a quiet link: its timer is due in the follower's 2 s.
        public TaskCompletionSource SettleTimerMade { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _made);
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(2))
                SettleTimerMade.TrySetResult();
            return timer;
        }
    }
}
