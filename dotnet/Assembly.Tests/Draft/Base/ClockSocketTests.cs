using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Assembly.Infrastructure.Clock;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Socket;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>
/// The backend clock over the socket (B9, plan 5.8): the <c>clock</c> topic, key <c>second</c>, on a fake clock. The
/// socket itself runs on the real clock, so moving the fake one only moves the backend clock.
/// </summary>
public sealed class ClockSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _file = new();
    private readonly CountingTime _time = new(Start);
    private AppDatabase _db = null!;
    private BackendClock _clock = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        _clock = new BackendClock(topics, _time);
        _clock.Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("clock-token", calls, "test-machine", TimeProvider.System);
        _app.Map("/socket", endpoint.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new Uri($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _clock.Dispose();
        _db.Dispose();
        _file.Dispose();
    }

    // Pins: base#9
    [Fact]
    public async Task A_second_subscriber_gets_180_changes_over_three_minutes()
    {
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"clock","key":"second"}""");
        Assert.Equal("""{"t":"state","sub":1,"items":[{"key":"second","item":{"now":""" + Ms(0) + "}}]}", await ReadAsync(socket));

        for (var second = 1; second <= 180; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(Change(1, second), await ReadAsync(socket));
        }
    }

    [Fact]
    public async Task The_clock_ticks_only_while_someone_is_subscribed()
    {
        Assert.Equal(0, _time.Timers);
        using var socket = await OpenAsync();
        await SendAsync(socket, """{"t":"sub","id":1,"topic":"clock","key":"second"}""");
        await ReadAsync(socket);
        Assert.Equal(1, _time.Timers);

        await SendAsync(socket, """{"t":"unsub","id":1}""");
        await SendAsync(socket, """{"t":"call","id":2,"feature":"nothing","method":"here","args":{}}""");
        Assert.Contains("\"not_found\"", await ReadAsync(socket));
        _time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => _time.Timers == 0);

        await SendAsync(socket, """{"t":"sub","id":3,"topic":"clock","key":"second"}""");
        await ReadAsync(socket);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Change(3, 2), await ReadAsync(socket));
    }

    private static long Ms(int seconds) => Start.AddSeconds(seconds).ToUnixTimeMilliseconds();

    private static string Change(int sub, int seconds) =>
        $"{{\"t\":\"change\",\"sub\":{sub},\"op\":\"upsert\",\"key\":\"second\",\"item\":{{\"now\":{Ms(seconds)}}}}}";

    private static async Task Until(Func<bool> done)
    {
        for (var waited = TimeSpan.Zero; !done(); waited += TimeSpan.FromMilliseconds(10))
        {
            Assert.True(waited < Wait, "it didn't happen within 10 s");
            await Task.Delay(10);
        }
    }

    private async Task<ClientWebSocket> OpenAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(_address, CancellationToken.None);
        await SendAsync(socket, """{"t":"hello","token":"clock-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    // A fake clock that counts its live timers, so a test can see the backend clock's timer stop.
    internal sealed class CountingTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _timers;
        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timers);
            return new Counted(base.CreateTimer(callback, state, dueTime, period), this);
        }

        private sealed class Counted(ITimer timer, CountingTime owner) : ITimer
        {
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref owner._timers);
                timer.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                Dispose();
                await ValueTask.CompletedTask;
            }
        }
    }
}
