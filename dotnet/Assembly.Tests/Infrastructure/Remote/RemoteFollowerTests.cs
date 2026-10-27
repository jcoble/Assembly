using System.Net;
using System.Net.WebSockets;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

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
    private readonly WatchedTopic _topic = new();
    private readonly LiveHub _hub;
    private readonly CountingTime _time = new(DateTimeOffset.UtcNow);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _remote;
    private RemoteFollower? _follower;

    public RemoteFollowerTests()
    {
        _database = TestDatabase.Open(_db.Path);
        _hub = new LiveHub(RowStore.Open(_database), topics: _topic);
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
            await Task.Delay(5);
        }

        Assert.Equal(made, _time.Made);
        Assert.False(_closed.Task.IsCompleted);
    }

    [Fact]
    public async Task A_follower_settles_as_soon_as_its_last_watcher_leaves()
    {
        var viewer = _hub.Subscribe("s1", 0);
        await FollowPastSettledAsync();

        viewer.Dispose();

        await _closed.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task A_session_watched_only_through_the_rows_topic_settles_a_Settled_after_the_watch_ends()
    {
        _topic.Watched = true;
        await FollowPastSettledAsync();
        Assert.False(_closed.Task.IsCompleted);

        _topic.Watched = false;
        _time.Advance(PastSettled);

        await _closed.Task.WaitAsync(Wait);
    }

    // Follows s1 on a remote whose link stays open and quiet, until the link has been quiet past Settled.
    private async Task FollowPastSettledAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _remote = builder.Build();
        _remote.UseWebSockets();
        _remote.Map("/live", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            _connected.TrySetResult();
            try
            {
                while ((await socket.ReceiveAsync(new byte[1024], context.RequestAborted)).MessageType != WebSocketMessageType.Close)
                {
                }
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException)
            {
            }
            _closed.TrySetResult();
        });
        await _remote.StartAsync();

        _follower = new RemoteFollower(new Uri(_remote.Urls.Single()), "box-token", _hub, _ => Task.CompletedTask, () => { },
            _ => Task.CompletedTask, (_, _) => { }, new SharedLog(), _time);
        _follower.Watch("s1");
        await _connected.Task.WaitAsync(Wait);
        await Task.Delay(100);
        _time.Advance(PastSettled);
        await Task.Delay(100);
    }

    public async ValueTask DisposeAsync()
    {
        if (_follower is not null)
            await _follower.DisposeAsync();
        if (_remote is not null)
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

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _made);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    // A rows-topic watcher on every session, while Watched.
    private sealed class WatchedTopic : ITopics
    {
        private volatile bool _watched;
        public bool Watched { get => _watched; set => _watched = value; }

        public void Publish(string topic, string? key, string json, bool keyedOnly = false)
        {
        }

        public bool HasSubscribers(string topic, string key) => _watched;
    }
}
