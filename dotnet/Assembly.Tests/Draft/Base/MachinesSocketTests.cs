using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Clock;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>
/// The <c>machines</c> topic over the socket, with a real remote machine whose far end is a small stub server: whether
/// each machine answered last time, and why not when it didn't. The probe rides the backend clock, on a fake clock.
/// </summary>
public sealed partial class MachinesSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string Local = """{"name":null,"kind":"local","reachable":true,"reason":null}""";
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _file = new();
    private readonly StubRemote _stub = new();
    private AppDatabase _db = null!;
    private LiveHub _hub = null!;
    private SessionStore _sessions = null!;
    private readonly ClockSocketTests.CountingTime _time = new(Start);
    private BackendClock _clock = null!;
    private BackgroundWork _work = null!;
    private MachineStatus _status = null!;
    private RemoteMachine _box = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        await _stub.StartAsync();
        var topics = new TopicSet();
        _db = AppDatabase.Open(_file.Path, TimeProvider.System, topics);
        _hub = new LiveHub(RowStore.Open(_db), topics: topics);
        _sessions = SessionStore.Open(_db, TimeProvider.System, "checkouts");
        var status = _status = new MachineStatus(topics, ["box"]);
        _box = new RemoteMachine("box", _stub.Address, "box-token", _hub, _sessions, TextWriter.Null, status);
        var machines = new MachineSet(new FakeMachine(null, _sessions), _box);
        var calls = new SocketDispatcher(_db.Writer, topics, TimeProvider.System, NullLogger.Instance);
        _work = new BackgroundWork(_time, TextWriter.Null);
        _clock = new BackendClock(topics, _time, _work);
        var feature = new MachinesFeature(machines, status, _clock.Start, _time);
        _clock.Ticked += feature.Tick;
        _clock.Register(calls);
        feature.Register(calls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("machines-token", calls, "test-machine", TimeProvider.System);
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
        await _work.StopAsync(Wait);
        await _box.DisposeAsync();
        await _stub.DisposeAsync();
        _hub.Dispose();
        _db.Dispose();
        _file.Dispose();
    }

    [Fact]
    public async Task A_machines_subscription_starts_with_every_machine_and_a_remote_not_checked_yet()
    {
        using var socket = await OpenAsync();

        Assert.Equal(List(1, Box(false, "not checked yet")), await SubAsync(socket, 1, "machines"));
    }

    [Fact]
    public async Task A_failed_call_sends_the_machines_change_before_its_error_frame()
    {
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        await CallAsync(socket, 2, "machines", "agents", """{"machine":"box"}""");
        Assert.Equal(Change(1, Box(true, null)), await ReadAsync(socket));
        Assert.Contains("\"result\":", await ReadAsync(socket));

        _stub.Drop = true;
        await CallAsync(socket, 3, "machines", "agents", """{"machine":"box"}""");

        Assert.Equal(Change(1, Box(false, "didn't answer")), await ReadAsync(socket));
        Assert.Contains("\"code\":\"unreachable\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_second_identical_outcome_publishes_nothing()
    {
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        await CallAsync(socket, 2, "machines", "agents", """{"machine":"box"}""");
        Assert.Equal(Change(1, Box(true, null)), await ReadAsync(socket));
        Assert.Contains("\"result\":", await ReadAsync(socket));

        // A change would be queued before the answer.
        await CallAsync(socket, 3, "machines", "agents", """{"machine":"box"}""");
        Assert.Contains("\"result\":", await ReadAsync(socket));
        _stub.Drop = true;
        await CallAsync(socket, 4, "machines", "agents", """{"machine":"box"}""");
        Assert.Equal(Change(1, Box(false, "didn't answer")), await ReadAsync(socket));
        Assert.Contains("\"code\":\"unreachable\"", await ReadAsync(socket));
        await CallAsync(socket, 5, "machines", "agents", """{"machine":"box"}""");
        Assert.Contains("\"code\":\"unreachable\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task The_first_tick_after_a_subscribe_checks_the_remote_s_format_and_every_15th_tick_after()
    {
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        await SubAsync(socket, 2, "clock", "second");

        await TickAsync(socket);
        Assert.Equal(Change(1, Box(true, null)), await ReadAsync(socket));
        Assert.Equal(1, _stub.VersionHits);

        _stub.DropVersion = true;
        for (var tick = 0; tick < 14; tick++)
            await TickAsync(socket);
        Assert.Equal(1, _stub.VersionHits);
        await TickAsync(socket);
        Assert.Equal(Change(1, Box(false, "didn't answer")), await ReadAsync(socket));
        Assert.Equal(2, _stub.VersionHits);
    }

    [Fact]
    public async Task With_nobody_watching_machines_no_probe_goes_out()
    {
        using var socket = await OpenAsync();
        await SubAsync(socket, 2, "clock", "second");
        for (var tick = 0; tick < 30; tick++)
            await TickAsync(socket);
        Assert.Equal(0, _stub.VersionHits);

        await SubAsync(socket, 1, "machines");
        await TickAsync(socket);

        Assert.Equal(Change(1, Box(true, null)), await ReadAsync(socket));
    }

    [Fact]
    public async Task A_remote_with_a_live_follower_link_is_not_probed()
    {
        _box.WatchRunning(["s1"], []);
        await Until(() => _stub.LiveHits == 1);
        // The link counts once the follower's connect returns, just after the stub sees it.
        await Task.Delay(200);
        using var socket = await OpenAsync();
        Assert.Equal(List(1, Box(true, null)), await SubAsync(socket, 1, "machines"));
        await SubAsync(socket, 2, "clock", "second");

        await TickAsync(socket);
        await Task.Delay(300);

        Assert.Equal(1, _stub.VersionHits);
    }

    [Fact]
    public async Task A_probe_that_hangs_gives_up_at_5_seconds()
    {
        _stub.HangVersion = true;
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        await SubAsync(socket, 2, "clock", "second");
        await TickAsync(socket);
        await Until(() => _stub.VersionHits == 1);

        for (var tick = 0; tick < 4; tick++)
            await TickAsync(socket);
        _time.Advance(TimeSpan.FromSeconds(1));

        // The clock's change and the probe's run on their own; either may come first.
        var frames = new[] { await ReadAsync(socket), await ReadAsync(socket) };
        Assert.Contains(Change(1, Box(false, "didn't answer")), frames);
        await Until(() => _time.Timers == 1);
    }

    [Fact]
    public async Task The_last_machines_subscriber_leaving_cancels_the_probe_in_flight()
    {
        _stub.HangVersion = true;
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        _time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => _stub.VersionHits == 1);

        await UnsubAsync(socket, 1);
        _time.Advance(TimeSpan.FromSeconds(1));

        await Until(() => _stub.VersionAborted == 1);
        await Until(() => _time.Timers == 0);
        Assert.Equal(List(3, Box(false, "not checked yet")), await SubAsync(socket, 3, "machines"));
    }

    [Fact]
    public async Task The_clock_runs_for_machines_alone_and_stops_when_neither_is_watched()
    {
        Assert.Equal(0, _time.Timers);
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        Assert.Equal(1, _time.Timers);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Change(1, Box(true, null)), await ReadAsync(socket));

        await UnsubAsync(socket, 1);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => _time.Timers == 0);

        await SubAsync(socket, 3, "machines");
        _time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => _stub.VersionHits == 2);
    }

    [Fact]
    public async Task A_session_the_follower_gave_up_on_is_followed_again_once_the_probe_gets_an_answer()
    {
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        _stub.DropVersion = true;
        _box.WatchRunning(["s1"], []);
        Assert.Equal(Change(1, Box(false, "didn't answer")), await ReadAsync(socket));

        _stub.DropVersion = false;
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Change(1, Box(true, null)), await ReadAsync(socket));

        await Until(() => _stub.LiveHits == 1);
    }

    [Fact]
    public async Task A_probe_with_another_session_s_link_open_follows_the_given_up_session_again()
    {
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        _stub.DropVersion = true;
        _box.WatchRunning(["s1"], []);
        Assert.Equal(Change(1, Box(false, "didn't answer")), await ReadAsync(socket));

        _stub.DropVersion = false;
        _box.WatchRunning(["s2"], []);
        Assert.Equal(Change(1, Box(true, null)), await ReadAsync(socket));
        await Until(() => _stub.LiveHits == 1);
        // The link counts once the follower's connect returns, just after the stub sees it.
        await Task.Delay(200);
        await SubAsync(socket, 2, "clock", "second");
        await TickAsync(socket);

        await Until(() => _stub.LiveHits == 2);
    }

    [Fact]
    public async Task A_follower_link_opened_after_the_tunnel_exits_shows_the_remote_reachable_with_no_probe()
    {
        using var socket = await OpenAsync();
        await SubAsync(socket, 1, "machines");
        await CallAsync(socket, 2, "machines", "agents", """{"machine":"box"}""");
        Assert.Equal(Change(1, Box(true, null)), await ReadAsync(socket));
        Assert.Contains("\"result\":", await ReadAsync(socket));
        _status.Unreachable("box", "ssh tunnel exited");
        Assert.Equal(Change(1, Box(false, "ssh tunnel exited")), await ReadAsync(socket));

        _box.WatchRunning(["s1"], []);

        Assert.Equal(Change(1, Box(true, null)), await ReadAsync(socket));
        Assert.Equal(1, _stub.VersionHits);
    }

    [Fact]
    public async Task A_title_read_that_fails_after_a_turn_leaves_the_title_and_the_follower_keeps_copying()
    {
        _sessions.Add("s1", "claude", "https://github.com/o/r", "box");
        _sessions.SetTitle("s1", "kept");
        _stub.LiveRows =
        [
            Row(1, "r1", """{"state":"done"}"""),
            Row(2, "r2", """{"state":"started"}"""),
        ];

        _box.WatchRunning(["s1"], []);

        await Until(() => _hub.LastSeq("s1") == 2);
        Assert.Equal(1, _stub.TitleHits);
        Assert.Equal("kept", _sessions.Get("s1")!.Title);
    }

    [Fact]
    public async Task A_500_answers_failed_and_leaves_the_machine_reachable()
    {
        _stub.AgentsStatus = 500;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "machines", "agents", """{"machine":"box"}""");
        var error = await ReadAsync(socket);
        Assert.Contains("\"code\":\"failed\"", error);
        Assert.Contains("box answered 500: boom", error);

        Assert.Equal(List(2, Box(true, null)), await ListAsync(socket, 2));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_401_or_403_answers_unreachable_with_the_token_reason(int refused)
    {
        _stub.AgentsStatus = refused;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "machines", "agents", """{"machine":"box"}""");
        var error = await ReadAsync(socket);
        Assert.Contains("\"code\":\"unreachable\"", error);
        Assert.Contains("the remote refused this computer's token", error);

        Assert.Equal(List(2, Box(false, "the remote refused this computer's token")), await ListAsync(socket, 2));
    }

    [Fact]
    public async Task A_transport_failure_is_didnt_answer_and_the_next_answer_is_reachable()
    {
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "machines", "agents", """{"machine":"box"}""");
        Assert.Contains("\"result\":", await ReadAsync(socket));
        Assert.Equal(List(2, Box(true, null)), await ListAsync(socket, 2));

        _stub.Drop = true;
        await CallAsync(socket, 3, "machines", "agents", """{"machine":"box"}""");
        Assert.Contains("\"code\":\"unreachable\"", await ReadAsync(socket));
        Assert.Equal(List(4, Box(false, "didn't answer")), await ListAsync(socket, 4));

        _stub.Drop = false;
        await CallAsync(socket, 5, "machines", "agents", """{"machine":"box"}""");
        Assert.Contains("\"result\":", await ReadAsync(socket));
        Assert.Equal(List(6, Box(true, null)), await ListAsync(socket, 6));
    }

    [Fact]
    public async Task A_remote_with_another_row_format_stays_unreachable_with_its_reason_on_each_call()
    {
        _stub.Format = 99;
        var reason = $"stores rows in format 99; this computer reads format {RowStore.FormatVersion}";
        using var socket = await OpenAsync();

        for (var id = 1; id <= 4; id += 2)
        {
            await CallAsync(socket, id, "machines", "agents", """{"machine":"box"}""");
            var error = await ReadAsync(socket);
            Assert.Contains("\"code\":\"unreachable\"", error);
            Assert.Contains(reason, error);
            Assert.Equal(List(id + 1, Box(false, reason)), await ListAsync(socket, id + 1));
        }
        Assert.Equal((2, 0), (_stub.VersionHits, _stub.AgentsHits));
    }

    [Theory]
    [InlineData(202, "stopped")]
    [InlineData(404, "not running")]
    [InlineData(500, "failed")]
    [InlineData(200, "failed")]
    public async Task Stopping_a_run_on_the_remote_fails_on_an_error_status_instead_of_saying_it_isnt_running(int status, string expected)
    {
        _stub.StopStatus = status;
        var session = new SessionInfo("s1", "claude", "https://github.com/o/r", "", null, 0);

        string outcome;
        try
        {
            outcome = await _box.StopAsync(session, "r1", CancellationToken.None) ? "stopped" : "not running";
        }
        catch (CallFailed)
        {
            outcome = "failed";
        }

        Assert.Equal(expected, outcome);
    }

    private static string Box(bool reachable, string? reason) =>
        $$"""{"name":"box","kind":"remote","reachable":{{(reachable ? "true" : "false")}},"reason":{{(reason is null ? "null" : $"\"{reason}\"")}}}""";

    private static string List(long sub, string box) =>
        $$$"""{"t":"state","sub":{{{sub}}},"items":[{"key":"local","item":{{{Local}}}},{"key":"box","item":{{{box}}}}]}""";

    private static string Change(long sub, string box) =>
        $$"""{"t":"change","sub":{{sub}},"op":"upsert","key":"box","item":{{box}}}""";

    private static string Row(long seq, string runId, string payload) =>
        $$$"""{"type":"row","row":{"sessionId":"s1","seq":{{{seq}}},"runId":"{{{runId}}}","itemId":null,"kind":"turn","payload":{{{payload}}},"createdAt":{{{seq}}},"prevSeq":{{{seq - 1}}}}}""";

    // The machines state, read through a subscription that's then dropped.
    private static async Task<string> ListAsync(WebSocket socket, long id)
    {
        var state = await SubAsync(socket, id, "machines");
        await UnsubAsync(socket, id);
        return state;
    }

    private static async Task<string> SubAsync(WebSocket socket, long id, string topic, string? key = null)
    {
        await SendAsync(socket, key is null ? $$"""{"t":"sub","id":{{id}},"topic":"{{topic}}"}""" : $$"""{"t":"sub","id":{{id}},"topic":"{{topic}}","key":"{{key}}"}""");
        return await ReadAsync(socket);
    }

    // An unsub has no answer; a call that isn't there answers once the unsub has run.
    private static async Task UnsubAsync(WebSocket socket, long id)
    {
        await SendAsync(socket, $$"""{"t":"unsub","id":{{id}}}""");
        await CallAsync(socket, 1000 + id, "nothing", "here", "{}");
        Assert.Contains("\"not_found\"", await ReadAsync(socket));
    }

    // One second on the fake clock, waited out through the clock's own change (sub 2), so ticks never pile up.
    private async Task TickAsync(WebSocket socket)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.StartsWith("""{"t":"change","sub":2,""", await ReadAsync(socket));
    }

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
        await SendAsync(socket, """{"t":"hello","token":"machines-token","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await ReadAsync(socket));
        return socket;
    }

    private static Task CallAsync(WebSocket socket, long id, string feature, string method, string args) =>
        SendAsync(socket, $$"""{"t":"call","id":{{id}},"feature":"{{feature}}","method":"{{method}}","args":{{args}}}""");

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReadAsync(WebSocket socket)
    {
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(Wait);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        // The error frames escape an apostrophe; compare on one spelling.
        return Encoding.UTF8.GetString(message.ToArray()).Replace("\\u0027", "'");
    }
}
