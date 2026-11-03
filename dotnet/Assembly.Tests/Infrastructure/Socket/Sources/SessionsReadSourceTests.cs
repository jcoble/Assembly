using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class SessionsReadSourceTests
{
    [Fact]
    public async Task Current_local_receipt_marks_only_through_actual_rows_and_publishes_after_commit()
    {
        await using var hosts = new TwoHosts();
        var sources = Observe(hosts.Mac); hosts.Mac.SessionSources = sources;
        await hosts.Start(); var last = Seed(hosts.Mac, "local-read");
        using var watcher = await Open(hosts.Mac.Address); using var reader = await Open(hosts.Mac.Address);
        await Send(watcher, "{\"t\":\"sub\",\"id\":9,\"topic\":\"sessions\",\"key\":\"local-read\"}");
        Assert.Equal("state", (await Read(watcher)).GetProperty("t").GetString());
        var expected = new ExpectedSource(hosts.Mac.Fixture.Machine, 0, null);
        AssertResult(await Mark(reader, 1, new("local-read", long.MaxValue, expected)));
        var changed = await Read(watcher);
        Assert.Equal("change", changed.GetProperty("t").GetString());
        Assert.False(changed.GetProperty("item").GetProperty("unread").GetBoolean());
        Assert.Equal(last, hosts.Mac.Sessions.Get("local-read")!.ReadSeq);
        await sources.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, sources.Disposals);
        Assert.Equal(expected, Assert.Single(sources.Captures));
    }

    [Fact]
    public async Task Explicit_source_without_bindings_refuses_but_legacy_read_keeps_its_existing_behavior()
    {
        await using var hosts = new TwoHosts(); await hosts.Start();
        var last = Seed(hosts.Mac, "legacy-read");
        using var socket = await Open(hosts.Mac.Address);
        AssertError(await Mark(socket, 1, new("legacy-read", last, new(hosts.Mac.Fixture.Machine, 0, null))), "source_changed");
        Assert.Equal(0, hosts.Mac.Sessions.Get("legacy-read")!.ReadSeq);
        var legacy = new SessionsFeature.ReadArgs("legacy-read", last);
        Assert.False(JsonSerializer.SerializeToElement(legacy, SessionsJson.Default.ReadArgs).TryGetProperty("expectedSource", out _));
        AssertResult(await Mark(socket, 2, legacy));
        Assert.Equal(last, hosts.Mac.Sessions.Get("legacy-read")!.ReadSeq);
    }

    [Fact]
    public async Task Contradictory_frame_source_and_arguments_refuse_before_capture_or_write()
    {
        await using var hosts = new TwoHosts(); var sources = Observe(hosts.Mac); hosts.Mac.SessionSources = sources;
        await hosts.Start(); var last = Seed(hosts.Mac, "frame-read");
        using var socket = await Open(hosts.Mac.Address);
        var expected = new ExpectedSource(hosts.Mac.Fixture.Machine, 0, null);
        AssertError(await Mark(socket, 1, new("frame-read", last, expected), expected with { Generation = 1 }), "bad_args");
        Assert.Empty(sources.Captures); Assert.Equal(0, hosts.Mac.Sessions.Get("frame-read")!.ReadSeq);
        AssertResult(await Mark(socket, 2, new("frame-read", last), expected));
        Assert.Equal(expected, Assert.Single(sources.Captures));
    }

    [Fact]
    public async Task Captured_remote_source_marks_its_matching_facade_but_cannot_mark_a_local_session_with_the_same_route_claim()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        var sources = Observe(hosts.Mac, () => hosts.Connections); hosts.Mac.SessionSources = sources;
        await hosts.Start(); Seed(hosts.Remote, "remote-read");
        var last = Seed(hosts.Mac, "remote-read", "workbox-test"); Seed(hosts.Mac, "local-only");
        await hosts.StartMirror(); var expected = Expected(hosts);
        using var socket = await Open(hosts.Mac.Address);
        AssertError(await Mark(socket, 1, new("local-only", last, expected)), "source_changed");
        Assert.Equal(0, hosts.Mac.Sessions.Get("local-only")!.ReadSeq);
        AssertResult(await Mark(socket, 2, new("remote-read", last, expected)));
        Assert.Equal(last, hosts.Mac.Sessions.Get("remote-read")!.ReadSeq);
        Assert.Equal(0, hosts.Remote.Sessions.Get("remote-read")!.ReadSeq);
        Assert.All(sources.Captures, source => Assert.Equal(expected, source));
    }

    [Fact]
    public async Task Remote_source_invalidated_at_writer_certification_writes_and_publishes_nothing()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        var sources = Observe(hosts.Mac, () => hosts.Connections); hosts.Mac.SessionSources = sources;
        await hosts.Start(); Seed(hosts.Remote, "held-read"); var last = Seed(hosts.Mac, "held-read", "workbox-test");
        await hosts.StartMirror(); var expected = Expected(hosts);
        using var socket = await Open(hosts.Mac.Address);
        var reached = Signal(); var release = Signal();
        sources.BeforeDisplay = (scope, ct) =>
        {
            if (scope is null) return;
            reached.TrySetResult(); release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).GetAwaiter().GetResult();
        };
        var published = 0;
        var subscription = hosts.Mac.Topics.Subscribe("sessions", "held-read", _ => { }, (_, _) => Interlocked.Increment(ref published));
        try
        {
            var audit = hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM audit WHERE what='sessions/read'");
            await SendMark(socket, 1, new("held-read", last, expected));
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disconnect = hosts.DisconnectMirror();
            await sources.Invalidated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.TrySetResult(); await disconnect;
            AssertError(await Read(socket), "source_changed");
            Assert.Equal(0, hosts.Mac.Sessions.Get("held-read")!.ReadSeq);
            Assert.Equal(audit, hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM audit WHERE what='sessions/read'"));
            Assert.Equal(0, published);
            await sources.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, sources.Disposals);
        }
        finally { release.TrySetResult(); hosts.Mac.Topics.Unsubscribe(subscription); }
    }

    [Fact]
    public async Task Cancelling_after_capture_disposes_the_dispatcher_guard_without_marking_or_publishing()
    {
        await using var hosts = new TwoHosts(); var sources = Observe(hosts.Mac); hosts.Mac.SessionSources = sources;
        await hosts.Start(); var last = Seed(hosts.Mac, "cancel-read");
        var reached = Signal();
        sources.BeforeCheck = ct => { reached.TrySetResult(); Task.Delay(Timeout.InfiniteTimeSpan, ct).GetAwaiter().GetResult(); };
        var published = 0;
        var subscription = hosts.Mac.Topics.Subscribe("sessions", "cancel-read", _ => { }, (_, _) => Interlocked.Increment(ref published));
        try
        {
            using var socket = await Open(hosts.Mac.Address);
            await SendMark(socket, 7, new("cancel-read", last, new(hosts.Mac.Fixture.Machine, 0, null)));
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Send(socket, "{\"t\":\"cancel\",\"id\":7}");
            AssertError(await Read(socket), "cancelled");
            await sources.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, sources.Disposals); Assert.Equal(0, published);
            Assert.Equal(0, hosts.Mac.Sessions.Get("cancel-read")!.ReadSeq);
        }
        finally { hosts.Mac.Topics.Unsubscribe(subscription); }
    }

    [Fact]
    public async Task Route_matching_facade_cannot_override_a_contradictory_actual_engine_owner()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        hosts.Mac.SessionSources = Observe(hosts.Mac, () => hosts.Connections);
        await hosts.Start(); Seed(hosts.Remote, "foreign-read"); var last = Seed(hosts.Mac, "foreign-read", "workbox-test");
        Assert.True(hosts.Mac.Fixture.Apply(new RegisterForeignSession("different-actual-owner", "codex", "Foreign session"),
            "foreign-read", actor: new(ActorKind.System, "machine:different-actual-owner")).Applied);
        await hosts.StartMirror(); using var socket = await Open(hosts.Mac.Address);
        AssertError(await Mark(socket, 1, new("foreign-read", last, Expected(hosts))), "source_changed");
        Assert.Equal(0, hosts.Mac.Sessions.Get("foreign-read")!.ReadSeq);
    }

    private static ObservedSources Observe(TwoHosts.Host host, Func<Assembly.Infrastructure.Remote.RemoteConnections>? connections = null) => new(() =>
        new SessionRowSources(host.Fixture.Db, host.Sessions, new MachineSet(new FakeMachine(null, host.Sessions)), connections?.Invoke()));
    private static ExpectedSource Expected(TwoHosts hosts) => hosts.Mac.Fixture.Db.QueryFirst("""
        SELECT machine,generation,name FROM engine_mirror WHERE machine=$machine AND state='live'
        """, row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default, new SqlArg("$machine", hosts.Remote.Fixture.Machine))!;
    private static long Seed(TwoHosts.Host host, string session, string? route = null)
    {
        host.Sessions.Add(session, "codex", "source-derived", route!);
        var rows = RowStore.Open(host.Fixture.Db); var converter = new RowConverter();
        rows.Commit(session, converter.Apply(new TurnStarted(1000, "source-derived-turn", "Historical row")));
        rows.Commit(session, converter.Apply(new TurnEnded(1001, "end_turn", null)));
        return rows.LastSeq(session);
    }
    private static async Task<ClientWebSocket> Open(Uri address)
    {
        var socket = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket"), timeout.Token);
        await Send(socket, "{\"t\":\"hello\",\"token\":\"isolated-token\",\"protocol\":1}");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static async Task<JsonElement> Mark(WebSocket socket, int id, SessionsFeature.ReadArgs args, ExpectedSource? frame = null)
    { await SendMark(socket, id, args, frame); return await Read(socket); }
    private static Task SendMark(WebSocket socket, int id, SessionsFeature.ReadArgs args, ExpectedSource? frame = null) => Send(socket,
        $$"""{"t":"call","id":{{id}},"feature":"sessions","method":"read","args":{{JsonSerializer.Serialize(args, SessionsJson.Default.ReadArgs)}}{{(frame is null ? "" : ",\"expectedSource\":" + JsonSerializer.Serialize(frame, SessionsJson.Default.ExpectedSource))}}}""");
    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[65536];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); Assert.Equal(WebSocketMessageType.Text, part.MessageType); bytes.Write(buffer, 0, part.Count); }
        while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
    }
    private static void AssertResult(JsonElement reply)
    { Assert.Equal("result", reply.GetProperty("t").GetString()); Assert.True(reply.GetProperty("result").GetProperty("ok").GetBoolean()); }
    private static void AssertError(JsonElement reply, string code)
    { Assert.Equal("error", reply.GetProperty("t").GetString()); Assert.Equal(code, reply.GetProperty("code").GetString()); Assert.False(reply.TryGetProperty("result", out _)); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ObservedSources(Func<IRowSourceBindings> source) : IRowSourceBindings
    {
        public readonly List<ExpectedSource> Captures = [];
        public Action<CancellationToken>? BeforeCheck;
        public Action<IReadScope?, CancellationToken>? BeforeDisplay;
        public TaskCompletionSource Disposed { get; } = Signal();
        public TaskCompletionSource Invalidated { get; } = Signal();
        public int Disposals;
        public async Task<RowSourceBinding> CaptureAsync(ExpectedSource expected, string session, CancellationToken ct)
        {
            Captures.Add(expected);
            return new ObservedBinding(await source().CaptureAsync(expected, session, ct), this, ct);
        }
        private sealed class ObservedBinding(RowSourceBinding inner, ObservedSources owner, CancellationToken ct) : RowSourceBinding
        {
            private int _disposed;
            private readonly CancellationTokenRegistration _changed = inner.Changed.Register(() => owner.Invalidated.TrySetResult());
            public override bool Remote => inner.Remote;
            public override CancellationToken Changed => inner.Changed;
            public override void EnsureCurrent() { owner.BeforeCheck?.Invoke(ct); inner.EnsureCurrent(); }
            public override void EnsureDisplayCurrent(IReadScope? scope = null)
            {
                owner.BeforeCheck?.Invoke(ct);
                owner.BeforeDisplay?.Invoke(scope, ct);
                inner.EnsureDisplayCurrent(scope);
            }
            public override void Watch() => inner.Watch();
            public override string? ActiveRun => inner.ActiveRun;
            public override Task<JsonElement> PageAsync(long before, int limit, CancellationToken cancellation) => inner.PageAsync(before, limit, cancellation);
            public override Task<SubagentPage> SubagentsAsync(string? after, int limit, CancellationToken cancellation) => inner.SubagentsAsync(after, limit, cancellation);
            public override async IAsyncEnumerable<JsonElement> WatchAsync(long? after, [EnumeratorCancellation] CancellationToken cancellation)
            { await foreach (var frame in inner.WatchAsync(after, cancellation)) yield return frame; }
            public override void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _changed.Dispose(); inner.Dispose(); Interlocked.Increment(ref owner.Disposals); owner.Disposed.TrySetResult(); } }
        }
    }
}
