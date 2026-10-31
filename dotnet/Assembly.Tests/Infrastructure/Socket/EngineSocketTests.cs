using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine.Sessions;
using Assembly.Application.Features.Engine.Runs;
using Assembly.Application.Features.Engine.NeedsMe;
using Assembly.Application.Features.Engine.Messages;
using Assembly.Application.Features.Engine.Handoffs;
using Assembly.Application.Features.Engine.Effects;
using Assembly.Application.Features.Engine.Prs;
using Assembly.Application.Features.Engine.Recording;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class EngineSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly TopicSet _topics = new();
    private EngineFixture _f = null!;
    private EngineRuntime _runtime = null!;
    private WebApplication _app = null!;
    private Uri _address = null!;
    private SnapshotGate _database = null!;
    private readonly TaskCompletionSource _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        _f = new(topics: _topics);
        _database = new(_f.Db);
        _runtime = EngineRuntime.Start(_database, _topics, _f.Time, () => 5);
        var calls = new SocketDispatcher(_f.Db.Writer, _topics, _f.Time, NullLogger.Instance, new EngineSocketStream(_runtime));
        new EngineCalls(_runtime, _f.Time).Register(calls);
        calls.Topic("sessions", TestJson.Default.Note, n => n.Text, (_, _) => [new Note("legacy")]);
        calls.Read("test", "barrier", TestJson.Default.Note, TestJson.Default.Note, (n, _) =>
        {
            _barrier.TrySetResult();
            return Task.FromResult(n);
        });
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        var endpoint = new SocketEndpoint("token", calls, _runtime.Machine, _f.Time);
        _app.Map("/socket", endpoint.HandleAsync);
        var bulk = new SocketEndpoint("token", calls, _runtime.Machine, _f.Time, bulk: true);
        _app.Map("/socket/bulk", bulk.HandleAsync);
        _app.Lifetime.ApplicationStopping.Register(endpoint.CloseAll);
        _app.Lifetime.ApplicationStopping.Register(bulk.CloseAll);
        await _app.StartAsync();
        var http = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _address = new($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _runtime.DisposeAsync();
        _f.Dispose();
    }

    [Theory]
    [InlineData("sessions", "session")]
    [InlineData("runs", "run")]
    [InlineData("needs_me", "needs_me")]
    [InlineData("messages", "message")]
    [InlineData("prs", "pr")]
    [InlineData("usage", "usage")]
    public async Task Each_topic_replays_since_and_snapshots_on_gap(string topic, string kind)
    {
        Event(kind, "one"); Event(kind, "other");
        await Published();
        using var socket = await Open();
        await Send(socket, $$"""{"t":"sub","id":7,"stream":"engine","topic":"{{topic}}","key":"one","after":0}""");
        var page = await Read(socket);
        Assert.Equal("engine_events", page.GetProperty("t").GetString());
        Assert.Equal(2, page.GetProperty("through").GetInt64());
        Assert.Equal("one", Assert.Single(page.GetProperty("events").EnumerateArray()).GetProperty("entityId").GetString());
        await Send(socket, """{"t":"unsub","id":7}""");
        _f.Db.Writer.Write("gap", s => s.Execute("DELETE FROM engine_event WHERE seq=1"));
        await Send(socket, $$"""{"t":"sub","id":8,"stream":"engine","topic":"{{topic}}","after":0}""");
        Assert.Equal("engine_snapshot", (await Read(socket)).GetProperty("t").GetString());
    }

    [Fact]
    public async Task Snapshot_race_and_quiet_filter_do_not_lose_events()
    {
        for (var i = 0; i < 205; i++) _f.SeedSession($"s{i:D3}");
        Event("session", "s000");
        await Published();
        _database.Armed = true;
        using var socket = await Open();
        await Send(socket, """{"t":"sub","id":7,"stream":"engine","topic":"sessions"}""");
        await _database.Entered.Task.WaitAsync(Wait);
        _f.SeedSession("new"); Event("session", "new"); Event("usage", "quiet");
        await Published();
        _database.Release.TrySetResult();
        var ids = new List<string>();
        var seqs = new List<long>();
        long through = 0;
        while (through < 3)
        {
            var page = await Read(socket);
            if (page.GetProperty("t").GetString() == "engine_snapshot")
            {
                Assert.Equal(1, page.GetProperty("through").GetInt64());
                ids.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetString()!));
                if (!page.GetProperty("more").GetBoolean()) through = 1;
            }
            else
            {
                Assert.Equal(through, page.GetProperty("after").GetInt64());
                through = page.GetProperty("through").GetInt64();
                seqs.AddRange(page.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()));
                ids.AddRange(page.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("entityId").GetString()!));
            }
        }
        Assert.Equal(Enumerable.Range(0, 205).Select(i => $"s{i:D3}").Append("new"), ids);
        Assert.Equal(3, through);
        Assert.Equal(new long[] { 2 }, seqs);
        Assert.Equal(seqs.Distinct(), seqs);
        await Send(socket, """{"t":"unsub","id":7}""");
        using var reconnected = await Open();
        await Send(reconnected, """{"t":"sub","id":8,"stream":"engine","topic":"sessions","after":1}""");
        var replay = await Read(reconnected);
        Assert.Equal(3, replay.GetProperty("through").GetInt64());
        Assert.Equal("new", Assert.Single(replay.GetProperty("events").EnumerateArray()).GetProperty("entityId").GetString());
    }

    [Fact]
    public async Task Window_has_seven_commands_without_system_privileges()
    {
        _f.SeedSession("owner");
        _f.SeedSession("target");
        _f.SeedSession("handoff-session");
        Assert.True(_f.Apply(new OpenNeed("target", null, null, "question", "Question", [], null, null, null),
            "need", actor: new(ActorKind.System, "host")).Applied);
        using var socket = await Open();
        var receipts = new List<CommandResult>
        {
            await Command(socket, new StartSession("codex", "model", "/test", "Test", null, null, null), "created"),
            await Command(socket, new StopSession(), "created", 1),
            await Command(socket, new StartRun("owner", "https://example.com/task", null, null, 60000,
                [new("step", "Step", true, 1, false, [])]), "run"),
            await Command(socket, new CancelRun(), "run", 1),
            await Command(socket, new ResolveNeed("dismissed", null), "need", 1),
            await Command(socket, new SendMessage(null, "target", "Hello", "prompt"), "message"),
            await Command(socket, new RequestHandoff("handoff-session", "target"), "handoff")
        };
        Assert.All(receipts, r => Assert.True(r.Applied, r.Error));
        Assert.Equal("owner", _f.Text("SELECT answered_by FROM engine_needs_me"));
        Assert.Equal(_runtime.Machine, _f.Text("SELECT machine FROM engine_session WHERE id='created'"));
        Assert.Equal(1000, _f.Number("SELECT created_ms FROM engine_session WHERE id='created'"));
        var events = _f.Count("engine_event");
        var effects = _f.Count("engine_outbox");
        var commands = _f.Count("engine_command_receipt");
        Assert.Equal("forbidden", (await Command(socket, new SetSessionState("idle", null, null), "created", 2)).Error);
        Assert.Equal("forbidden", (await Command(socket, new CompleteEffect(new(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement("ok", EngineJson.Default.String), null)), "effect")).Error);
        Assert.Equal("forbidden", (await Command(socket, new ResolveNeed("expired", null), "need", 2)).Error);
        var json = JsonSerializer.SerializeToElement("test", EngineJson.Default.String);
        foreach (var denied in new EngineCommand[] {
            new ClaimEffect(), new RecoverEffect(), new EnableEffect(),
            new SetStepState("skipped", 0, [], null, true), new StartStep("target"),
            new ToolStarted(new("target", null, null, null, 0), "local", "tool", "read", json, 4),
            new ToolEnded(new("target", null, null, null, 0), "ok", json, 4),
            new TurnUsage(new("target", null, null, null, 0), "turn", 1, 2, null, null, null),
            new ReportPr("run", "repo", 1, "head", "https://example.com/pr", "open"),
            new ObservePr("open", "head", 0), new MessageOutcome("failed", null, null),
            new OpenNeed("target", null, null, "question", "Question", [], null, null, null),
            new ResolveNeed("cancelled", null) })
            Assert.Equal("forbidden", (await Command(socket, denied, "missing")).Error);
        Assert.Equal(events, _f.Count("engine_event"));
        Assert.Equal(effects, _f.Count("engine_outbox"));
        Assert.Equal(commands, _f.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Window_requests_handoff_with_session_version_and_epoch_but_cannot_report_outcome()
    {
        _f.SeedSession("session", epoch: 7);
        _f.Db.Writer.Write("session-version", s => s.Execute("UPDATE engine_session SET version=3 WHERE id='session'"));
        using var socket = await Open();
        Assert.Equal("stale_epoch", (await Command(socket, new RequestHandoff("session", "target"), "handoff", version: 3, epoch: 6)).Error);
        Assert.Equal("version_conflict", (await Command(socket, new RequestHandoff("session", "target"), "handoff", version: 0, epoch: 7)).Error);
        Assert.Equal(0, _f.Count("engine_handoff"));
        var requested = await Command(socket, new RequestHandoff("session", "target"), "handoff", version: 3, epoch: 7);
        Assert.True(requested.Applied, requested.Error);
        Assert.Equal(8, requested.Epoch);
        Assert.Equal("handing_off", _f.State("engine_session", "session"));
        Assert.Equal(8, _f.Number("SELECT reserved_epoch FROM engine_session WHERE id='session'"));
        await Published();
        await Send(socket, """{"t":"sub","id":7,"stream":"engine","topic":"sessions","key":"session","after":0}""");
        var page = await Read(socket);
        Assert.Equal("engine_events", page.GetProperty("t").GetString());
        var changed = Assert.Single(page.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("kind").GetString() == "session.changed");
        Assert.Equal("session", changed.GetProperty("entityId").GetString());
        Assert.Equal(4, changed.GetProperty("version").GetInt64());
        Assert.Equal("handing_off", changed.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal(8, changed.GetProperty("data").GetProperty("epoch").GetInt64());
        await Send(socket, """{"t":"unsub","id":7}""");
        Assert.Equal("forbidden", (await Command(socket, new HandoffOutcome("accepted", 8, null), "handoff", 1, 8)).Error);
        var target = new EngineActor(ActorKind.System, "target");
        Assert.Equal("stale_epoch", _f.Apply(new HandoffOutcome("accepted", 8, null), "handoff", 1, 7, target).Error);
        Assert.Equal("version_conflict", _f.Apply(new HandoffOutcome("accepted", 8, null), "handoff", 0, 8, target).Error);
        Assert.True(_f.Apply(new HandoffOutcome("accepted", 8, null), "handoff", 1, 8, target).Applied);
        var fenced = await Command(socket, new StopSession(), "session", 4, 7);
        Assert.Equal("handing_off", fenced.Error);
        Assert.Equal(8, fenced.Epoch);
        Assert.Equal("accepted", _f.State("engine_handoff", "handoff"));
    }

    [Fact]
    public async Task Stream_lifetime_preserves_existing_socket_contract()
    {
        Event("session", "first"); Event("session", "second");
        await Published();
        using var socket = await Open();
        await Send(socket, """{"t":"sub","id":1,"topic":"sessions"}""");
        var legacy = await Read(socket);
        Assert.Equal("state", legacy.GetProperty("t").GetString());
        Assert.Equal("legacy", Assert.Single(legacy.GetProperty("items").EnumerateArray()).GetProperty("key").GetString());
        await Send(socket, """{"t":"sub","id":2,"stream":"engine","topic":"prs","after":0}""");
        var progress = await Read(socket);
        Assert.Equal(2, progress.GetProperty("through").GetInt64());
        Assert.Empty(progress.GetProperty("events").EnumerateArray());
        Assert.Equal(0, _runtime.Publisher.MinimumReaderAck);
        await Send(socket, """{"t":"ack","id":2,"stream":"engine","after":1}""");
        await Barrier(socket);
        Assert.Equal(1, _runtime.Publisher.MinimumReaderAck);
        foreach (var frame in new[] {
            """{"t":"ack","id":2,"stream":"engine","after":3}""",
            """{"t":"ack","id":2,"stream":"engine","after":0}""",
            """{"t":"ack","id":99,"stream":"engine","after":1}""",
            """{"t":"ack","id":1,"stream":"engine","after":1}""",
            """{"t":"ack","id":2,"after":1}""" })
        {
            await Send(socket, frame);
            Assert.Equal("error", (await Read(socket)).GetProperty("t").GetString());
            Assert.Equal(1, _runtime.Publisher.MinimumReaderAck);
        }
        await Send(socket, """{"t":"ack","id":2,"stream":"engine","after":2}""");
        await Barrier(socket);
        Assert.Equal(2, _runtime.Publisher.MinimumReaderAck);
        await Send(socket, """{"t":"unsub","id":2}""");
        await Send(socket, """{"t":"unsub","id":1}""");
        await Barrier(socket);
        Event("session", "third");
        await Published();
        await Barrier(socket); // Any leaked engine frames would arrive before this result.
        Assert.False(_topics.HasSubscribers("sessions"));
    }

    [Fact]
    public async Task Closing_cancels_an_in_progress_snapshot()
    {
        _f.SeedSession();
        _database.Armed = true;
        using var socket = await Open();
        await Send(socket, """{"t":"sub","id":7,"stream":"engine","topic":"sessions"}""");
        await _database.Entered.Task.WaitAsync(Wait);
        socket.Abort();
        await _database.Cancelled.Task.WaitAsync(Wait);
        Assert.True(_database.Cancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Strict_command_arguments_and_unknown_discriminators_do_not_mutate()
    {
        using var socket = await Open();
        foreach (var command in new[] { """{"kind":"unknown"}""", """{"kind":"stop_session","actor":"system"}""" })
        {
            await Send(socket, $$$"""{"t":"call","id":10,"feature":"engine","method":"command","args":{"commandId":"{{{Guid.NewGuid()}}}","entityId":"s","expectedVersion":0,"epoch":0,"command":{{{command}}}}}""");
            Assert.Equal("bad_args", (await Read(socket)).GetProperty("code").GetString());
        }
        Assert.Equal(0, _f.Count("engine_command_receipt"));
        Assert.Equal(0, _f.Count("engine_event"));
    }

    [Fact]
    public async Task Reconnect_keeps_owner_identity_and_server_time_for_idempotent_commands()
    {
        var args = JsonSerializer.Serialize(new WindowCommand(Guid.NewGuid(), "s", 0, 0,
            new StartSession("codex", "model", "/test", "Title", null, null, null)), EngineSocketJson.Default.WindowCommand);
        var frame = $$"""{"t":"call","id":1,"feature":"engine","method":"command","args":{{args}}}""";
        using var socket = await Open();
        await Send(socket, frame);
        var first = (await Read(socket)).GetProperty("result");
        Assert.True(first.GetProperty("applied").GetBoolean());
        _f.Time.Advance(TimeSpan.FromSeconds(1));
        using var reconnected = await Open();
        await Send(reconnected, frame);
        Assert.Equal(first.GetRawText(), (await Read(reconnected)).GetProperty("result").GetRawText());
        Assert.Equal(1, _f.Count("engine_command_receipt"));
        Assert.Equal(1000, _f.Number("SELECT created_ms FROM engine_session WHERE id='s'"));
        // Host authority cannot be supplied as an extra frame argument.
        var forged = args[..^1] + ",\"actor\":{\"kind\":2,\"id\":\"host\"},\"machine\":\"forged\",\"nowMs\":999}";
        await Send(reconnected, $$"""{"t":"call","id":2,"feature":"engine","method":"command","args":{{forged}}}""");
        Assert.Equal("bad_args", (await Read(reconnected)).GetProperty("code").GetString());
        Assert.Equal(1, _f.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Retained_cursor_gap_snapshots_but_the_floor_itself_replays()
    {
        Event("session", "one"); Event("session", "two");
        await Published();
        _f.Db.Writer.Write("retain", s => s.Execute("DELETE FROM engine_event WHERE seq=1; UPDATE engine_meta SET retained_after=1"));
        using var socket = await Open();
        await Send(socket, """{"t":"sub","id":1,"stream":"engine","topic":"sessions","after":0}""");
        Assert.Equal("engine_snapshot", (await Read(socket)).GetProperty("t").GetString());
        await Send(socket, """{"t":"sub","id":2,"stream":"engine","topic":"sessions","after":1}""");
        var page = await Read(socket);
        Assert.Equal("engine_events", page.GetProperty("t").GetString());
        Assert.Equal(2, Assert.Single(page.GetProperty("events").EnumerateArray()).GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Entity_uses_bulk_socket_and_usage_uses_the_normal_socket()
    {
        _f.SeedSession();
        using var socket = await Open();
        await Send(socket, """{"t":"call","id":1,"feature":"engine","method":"entity","args":{"entityKind":"session","entityId":"s"}}""");
        Assert.Equal("not_found", (await Read(socket)).GetProperty("code").GetString());
        using var bulk = await Open(bulk: true);
        await Send(bulk, """{"t":"call","id":2,"feature":"engine","method":"entity","args":{"entityKind":"session","entityId":"s"}}""");
        Assert.Equal("s", (await Read(bulk)).GetProperty("result").GetProperty("id").GetString());
        await Send(socket, """{"t":"call","id":3,"feature":"engine","method":"usage","args":{"entityKind":"session","entityId":"s"}}""");
        Assert.Equal(0, (await Read(socket)).GetProperty("result").GetProperty("toolCalls").GetInt64());
        await Send(bulk, """{"t":"sub","id":4,"stream":"engine","topic":"sessions"}""");
        Assert.Equal("error", (await Read(bulk)).GetProperty("t").GetString());
    }

    [Fact]
    public async Task Snapshot_buffer_overflow_closes_and_reconnect_can_snapshot()
    {
        _f.SeedSession();
        _database.Armed = true;
        using var socket = await Open();
        await Send(socket, """{"t":"sub","id":7,"stream":"engine","topic":"sessions"}""");
        await _database.Entered.Task.WaitAsync(Wait);
        _f.Db.Writer.Write("overflow", scope =>
        {
            for (var i = 0; i < 1200; i++)
                EngineJournal.Commit(scope, new(Guid.NewGuid(), _f.Machine, new(ActorKind.System, "test"), "s", 0, 0, 1000),
                    0, 0, [new("changed", "session", "s", 0, false, new(null, null, null, null, new string('x', 15000), null))], []);
            return 0;
        });
        await _database.Cancelled.Task.WaitAsync(Wait);
        await Assert.ThrowsAsync<WebSocketException>(async () => await Read(socket));
        _database.Armed = false;
        using var reconnected = await Open();
        await Send(reconnected, """{"t":"sub","id":8,"stream":"engine","topic":"sessions"}""");
        Assert.Equal(1200, (await Read(reconnected)).GetProperty("through").GetInt64());
    }

    [Fact]
    public async Task Bad_token_and_oversized_frames_cannot_reach_the_engine()
    {
        using (var unauthorized = new ClientWebSocket())
        {
            await unauthorized.ConnectAsync(_address, CancellationToken.None);
            await Send(unauthorized, """{"t":"hello","token":"wrong","protocol":1}""");
            var response = await unauthorized.ReceiveAsync(new ArraySegment<byte>(new byte[100]), CancellationToken.None).WaitAsync(Wait);
            Assert.Equal(WebSocketMessageType.Close, response.MessageType);
            Assert.Equal(4401, (int)unauthorized.CloseStatus!);
        }
        using var socket = await Open();
        await Send(socket, new string('x', SocketConnection.MaxFrame + 1));
        var closed = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[100]), CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
        Assert.Equal(0, _f.Count("engine_event"));
        Assert.Equal(0, _f.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Unsub_drops_queued_frames_for_both_subscription_lifetimes()
    {
        // Hold the sender on a result while both kinds of frames wait behind it.
        using var pair = new SocketPair();
        var calls = new SocketDispatcher(_f.Db.Writer, _topics, _f.Time, NullLogger.Instance, new EngineSocketStream(_runtime));
        calls.Topic("sessions", TestJson.Default.Note, n => n.Text, (_, _) => [new Note("legacy")]);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _topics.Unsubscribed += (_, _) => unsubscribed.TrySetResult();
        ISocketSubscription? engine = null;
        var connection = new SocketConnection(pair.Server, calls, _f.Time);
        var running = connection.RunAsync(Encoding.UTF8.GetBytes("token"), _runtime.Machine, CancellationToken.None);
        await Send(pair.Client, """{"t":"hello","token":"token","protocol":1}""");
        await Read(pair.Client);
        pair.BlockSends = true;
        connection.Send(SocketDispatcher.Frame(new ResultFrame(99, JsonSerializer.SerializeToElement("blocked", EngineJson.Default.String)), SocketJson.Default.ResultFrame));
        await pair.Entered.Task.WaitAsync(Wait);
        // The legacy sub is routed by the real connection map; the engine callback queues the same lifetime type.
        await Send(pair.Client, """{"t":"sub","id":1,"topic":"sessions"}""");
        engine = new EngineSocketStream(_runtime).Subscribe(2, "sessions", null, null, (text, from) =>
        {
            connection.Send(Encoding.UTF8.GetBytes(text), from);
            queued.TrySetResult();
        }, CancellationToken.None, () => { });
        await queued.Task.WaitAsync(Wait);
        engine.Dispose();
        await engine.Completion.WaitAsync(Wait);
        await Send(pair.Client, """{"t":"unsub","id":1}""");
        // An invalid ack is a receive-loop barrier, also queued behind the held sender.
        await Send(pair.Client, """{"t":"ack","id":999,"stream":"engine","after":0}""");
        // The legacy unsubscribe event proves its queued state has closed before releasing the send.
        await unsubscribed.Task.WaitAsync(Wait);
        pair.Release.TrySetResult();
        Assert.Equal("result", (await Read(pair.Client)).GetProperty("t").GetString());
        Assert.Equal("error", (await Read(pair.Client)).GetProperty("t").GetString());
        await pair.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await running.WaitAsync(Wait);
    }

    private async Task<CommandResult> Command(WebSocket socket, EngineCommand command, string id, long version = 0, long epoch = 0)
    {
        var args = JsonSerializer.Serialize(new WindowCommand(Guid.NewGuid(), id, version, epoch, command), EngineSocketJson.Default.WindowCommand);
        await Send(socket, $$"""{"t":"call","id":10,"feature":"engine","method":"command","args":{{args}}}""");
        var frame = await Read(socket);
        Assert.Equal("result", frame.GetProperty("t").GetString());
        return frame.GetProperty("result").Deserialize(EngineJson.Default.CommandResult)!;
    }

    private async Task Barrier(WebSocket socket)
    {
        await Send(socket, """{"t":"call","id":100,"feature":"test","method":"barrier","args":{}}""");
        Assert.Equal("result", (await Read(socket)).GetProperty("t").GetString());
    }

    private async Task Published()
    {
        var high = _runtime.Reader.HighWatermark(default);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _runtime.Publisher.Register(e => { if (e.Seq >= high) done.TrySetResult(); }, out var cut);
        if (cut >= high) return;
        await done.Task.WaitAsync(Wait);
        Assert.Equal(high, _runtime.Publisher.PublishedThrough);
    }

    [Fact]
    public async Task Unknown_stream_and_bad_cursors_are_errors_without_mutation()
    {
        using var socket = await Open();
        foreach (var input in new[] {
            """{"t":"sub","id":1,"stream":"other","topic":"sessions"}""",
            """{"t":"sub","id":1,"stream":"engine","topic":"other"}""",
            """{"t":"sub","id":1,"stream":"engine","topic":"sessions","after":-1}""",
            """{"t":"sub","id":1,"stream":"engine","topic":"sessions","after":1}""",
            """{"t":"sub","id":1,"stream":"engine","topic":"sessions","key":" "}""" })
        {
            await Send(socket, input);
            Assert.Equal("error", (await Read(socket)).GetProperty("t").GetString());
        }
        Assert.Equal(0, _f.Count("engine_event"));
        Assert.Equal(0, _f.Count("engine_command_receipt"));
    }

    private void Event(string kind, string id) => _f.Db.Writer.Write("event", s => EngineJournal.Commit(s,
        new(Guid.NewGuid(), _f.Machine, new(ActorKind.System, "test"), id, 0, 0, 1000),
        0, 0, [new("changed", kind, id, 0, false, new(null, null, null, null, null, null))], []));

    private async Task<ClientWebSocket> Open(bool bulk = false)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(bulk ? new(_address + "/bulk") : _address, CancellationToken.None);
        await Send(socket, """{"t":"hello","token":"token","protocol":1}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString());
        return socket;
    }

    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(Wait);
        using var buffer = new MemoryStream();
        var bytes = new byte[65536];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes, timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            buffer.Write(bytes, 0, result.Count);
        } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(buffer.ToArray());
        return json.RootElement.Clone();
    }

    private sealed class SnapshotGate(IAppDatabase database) : IAppDatabase
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAtomicWriter Writer => database.Writer;
        public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => database.Query(sql, map, cancel, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => database.QueryFirst(sql, map, cancel, args);
        public IReadSnapshot OpenSnapshot(CancellationToken cancellationToken) => new GatedSnapshot(database.OpenSnapshot(cancellationToken), this, cancellationToken);

        private sealed class GatedSnapshot(IReadSnapshot snapshot, SnapshotGate gate, CancellationToken cancellation) : IReadSnapshot
        {
            public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args)
            {
                if (gate.Armed && sql.Contains("SELECT kind,id,item"))
                {
                    gate.Entered.TrySetResult();
                    try { gate.Release.Task.WaitAsync(Wait, cancellation).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { gate.Cancelled.TrySetResult(); throw; }
                }
                return snapshot.Query(sql, map, args);
            }
            public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => snapshot.QueryFirst(sql, map, args);
            public void Dispose() => snapshot.Dispose();
        }
    }

    // Real WebSocket framing over loopback TCP, with a completion-gated server sender.
    private sealed class SocketPair : IDisposable
    {
        private readonly System.Net.Sockets.TcpClient _client = new();
        private readonly System.Net.Sockets.TcpClient _server;
        public bool BlockSends { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WebSocket Client { get; }
        public WebSocket Server { get; }
        public SocketPair()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _client.Connect((IPEndPoint)listener.LocalEndpoint);
            _server = listener.AcceptTcpClient();
            Client = WebSocket.CreateFromStream(_client.GetStream(), false, null, Timeout.InfiniteTimeSpan);
            Server = new GatedSocket(WebSocket.CreateFromStream(_server.GetStream(), true, null, Timeout.InfiniteTimeSpan), this);
        }
        public void Dispose()
        {
            Release.TrySetResult();
            Client.Dispose(); Server.Dispose(); _client.Dispose(); _server.Dispose();
        }

        private sealed class GatedSocket(WebSocket socket, SocketPair gate) : WebSocket
        {
            public override WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
            public override string? CloseStatusDescription => socket.CloseStatusDescription;
            public override WebSocketState State => socket.State;
            public override string? SubProtocol => socket.SubProtocol;
            public override void Abort() => socket.Abort();
            public override void Dispose() => socket.Dispose();
            public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => socket.CloseAsync(status, description, token);
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => socket.CloseOutputAsync(status, description, token);
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => socket.ReceiveAsync(buffer, token);
            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
            {
                if (gate.BlockSends)
                {
                    gate.Entered.TrySetResult();
                    await gate.Release.Task.WaitAsync(Wait, token);
                }
                await socket.SendAsync(buffer, type, end, token);
            }
        }
    }
}
