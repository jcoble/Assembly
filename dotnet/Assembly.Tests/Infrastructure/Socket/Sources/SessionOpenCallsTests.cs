using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Socket;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class SessionOpenCallsTests
{
    [Fact]
    public async Task Owner_control_endpoint_returns_recorded_engine_history_and_native_descriptors_without_starting_work()
    {
        await using var hosts = new TwoHosts();
        OpenGate gate = null!;
        hosts.Mac.AdditionalFeatures = calls => Register(hosts.Mac, gate = new(calls));
        await hosts.Start();
        var engine = Seed(hosts.Mac, "engine-local", engine: true, root: "/recorded-checkout");
        var history = Seed(hosts.Mac, "history-local");
        var native = Native(hosts.Mac);
        var before = hosts.Mac.Fixture.Count("engine_session");
        using var socket = await Open(hosts.Mac.Address);
        var read = Result(await Call(socket, 1, engine));
        AssertIdentity(read, engine, 0, null);
        Assert.False(read.ReadOnly); Assert.False(read.TranscriptOnly);
        Assert.Equal("/recorded-checkout", read.CheckoutRoot);
        var historical = Result(await Call(socket, 2, history));
        AssertIdentity(historical, history, 0, null);
        Assert.True(historical.ReadOnly); Assert.True(historical.HasHistoryRows);
        Assert.Null(historical.CheckoutRoot); Assert.True(historical.TranscriptOnly);
        var child = Result(await Call(socket, 3, native));
        AssertIdentity(child, native, 0, null);
        Assert.Equal("recorded-child", child.SessionId);
        Assert.Equal(native.Session, child.ParentSessionId);
        Assert.Equal(native.ItemId, child.ItemId); Assert.Equal(native.Seq, child.Seq);
        Assert.Equal(native.PayloadSessionId, child.PayloadSessionId);
        Assert.True(child.ReadOnly); Assert.True(child.HasHistoryRows); Assert.True(child.TranscriptOnly);
        Assert.Equal(before, hosts.Mac.Fixture.Count("engine_session"));
        Assert.Equal(0, hosts.Mac.Fixture.Count("engine_outbox"));
        Assert.Equal(3, gate.Requests);
        using var bulk = await Open(hosts.Mac.Address, bulk: true);
        AssertError(await Call(bulk, 4, history), "not_found");
    }

    [Fact]
    public async Task Phone_and_unidentified_callers_cannot_read_even_an_existing_local_target()
    {
        await using var hosts = new TwoHosts(); OpenGate gate = null!;
        hosts.Mac.AdditionalFeatures = calls => Register(hosts.Mac, gate = new(calls));
        await hosts.Start();
        var target = Seed(hosts.Mac, "private-history");
        using var socket = await Open(hosts.Mac.Address);
        gate.OverridePeer = true;
        foreach (var denied in new CallPeer?[] { new PhonePeer("actual-device"), null })
        {
            gate.Peer = denied;
            AssertError(await Call(socket, 1, target), "forbidden");
        }
        gate.OverridePeer = false;
        AssertIdentity(Result(await Call(socket, 2, target)), target, 0, null);
    }

    [Fact]
    public async Task Remote_descriptor_comes_from_the_actual_owner_and_rebinds_only_its_live_outer_source()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        OpenGate owner = null!; ICallRegistry macCalls = null!;
        hosts.Remote.AdditionalFeatures = calls => Register(hosts.Remote, owner = new(calls));
        hosts.Mac.AdditionalFeatures = calls => macCalls = calls;
        await hosts.Start();
        var target = Seed(hosts.Remote, "remote-history", root: "/recorded-owner-root");
        // A local facade under the saved route is not the owner's recorded checkout or history.
        hosts.Mac.Sessions.Add(target.Session, "codex", "repo", "workbox-test");
        await hosts.StartMirror();
        Register(hosts.Mac, new OpenGate(macCalls), hosts.Connections);
        using var socket = await Open(hosts.Mac.Address);
        var read = Result(await Call(socket, 1, target));
        Assert.True(read.Generation > 0);
        AssertIdentity(read, target, read.Generation, "workbox-test");
        Assert.Equal("/recorded-owner-root", read.CheckoutRoot);
        Assert.True(read.ReadOnly); Assert.True(read.HasHistoryRows);
        Assert.Equal(1, owner.Requests);
        Assert.Equal(0, hosts.Mac.Fixture.Count("engine_outbox"));
        Assert.Equal(0, hosts.Remote.Fixture.Count("engine_outbox"));
    }

    [Theory]
    [InlineData("missing_identity")]
    [InlineData("wrong_request")]
    [InlineData("changed_card")]
    public async Task Malformed_or_mismatched_owner_reply_never_becomes_a_readable_remote_target(string corruption)
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        OpenGate owner = null!; ICallRegistry macCalls = null!;
        hosts.Remote.AdditionalFeatures = calls => Register(hosts.Remote, owner = new(calls));
        hosts.Mac.AdditionalFeatures = calls => macCalls = calls;
        await hosts.Start(); var target = Native(hosts.Remote);
        await hosts.StartMirror();
        Register(hosts.Mac, new OpenGate(macCalls), hosts.Connections);
        owner.Mutate = value =>
        {
            if (corruption == "missing_identity") value.Remove("originalKey");
            else if (corruption == "wrong_request") value["requestId"] = target.RequestId + 1;
            else value["seq"] = target.Seq!.Value + 1;
        };
        using var socket = await Open(hosts.Mac.Address);
        AssertError(await Call(socket, 1, target), "source_changed");
        Assert.Equal(1, owner.Requests);
    }

    [Fact]
    public async Task A_source_invalidated_while_its_owner_reply_is_held_returns_no_descriptor()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        OpenGate owner = null!; ICallRegistry macCalls = null!;
        hosts.Remote.AdditionalFeatures = calls => Register(hosts.Remote, owner = new(calls));
        hosts.Mac.AdditionalFeatures = calls => macCalls = calls;
        await hosts.Start(); var target = Seed(hosts.Remote, "remote-held");
        await hosts.StartMirror();
        Register(hosts.Mac, new OpenGate(macCalls), hosts.Connections);
        var entered = Signal(); var release = Signal();
        owner.AfterReply = async call => { entered.TrySetResult(); await release.Task.WaitAsync(call.Cancellation); };
        using var socket = await Open(hosts.Mac.Address);
        await SendCall(socket, 1, target);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await hosts.DisconnectMirror();
        release.TrySetResult();
        AssertError(await Read(socket), "source_changed");
        Assert.Equal(1, owner.Requests);
    }

    [Fact]
    public async Task Cancelling_a_read_disposes_its_captured_server_guard_before_the_next_request()
    {
        await using var hosts = new TwoHosts(); OpenGate gate = null!;
        hosts.Mac.AdditionalFeatures = calls => Register(hosts.Mac, gate = new(calls));
        await hosts.Start(); var target = Seed(hosts.Mac, "cancel-history");
        var entered = Signal(); var disposed = Signal();
        gate.OnDispose = () => disposed.TrySetResult();
        gate.AfterReply = async call =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Cancellation);
        };
        using var socket = await Open(hosts.Mac.Address);
        await SendCall(socket, 7, target);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Send(socket, "{\"t\":\"cancel\",\"id\":7}");
        AssertError(await Read(socket), "cancelled");
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, gate.Disposals);
        gate.AfterReply = null;
        AssertIdentity(Result(await Call(socket, 8, target)), target, 0, null);
    }

    private static void Register(TwoHosts.Host host, ICallRegistry calls, Assembly.Infrastructure.Remote.RemoteConnections? connections = null) =>
        new SessionOpenCalls(host.Fixture.Db, host.Runtime, host.Sessions,
            new MachineSet(new FakeMachine(null, host.Sessions)), connections).Register(calls);

    private static SessionOpenTarget Seed(TwoHosts.Host host, string id, bool engine = false, string? root = null)
    {
        host.Sessions.Add(id, "codex", "source-derived-repo", machine: null!);
        if (engine) host.Fixture.SeedSession(id);
        if (root is not null) host.Fixture.Db.Writer.Write("test.recorded-root", scope => { host.Sessions.SetCheckout(scope, id, root, null); return 0; });
        History(host, id);
        return new(engine ? "engine" : "historical", host.Fixture.Machine, id, RequestId: 71);
    }

    private static SessionOpenTarget Native(TwoHosts.Host host)
    {
        var parent = Seed(host, "recorded-parent");
        var info = host.Sessions.Get(parent.Session)!;
        host.Sessions.Add("recorded-child", "codex", "source-derived-repo", machine: info.Machine!);
        host.Fixture.Db.Writer.Write("test.persisted-native-child", scope => scope.Execute("""
            UPDATE session_info SET parent_session_id=$parent,acp_session_id='native-child',helper=0
            WHERE session_id='recorded-child'
            """, new SqlArg("$parent", info.SessionId)));
        History(host, "recorded-child");
        // SOURCE-DERIVED persisted display facts, not a provider capture or hydration proof.
        var payload = new SubagentPayload(info.SessionId, "Recorded native card", null, "idle", null, null, false, null);
        var rows = RowStore.Open(host.Fixture.Db); var batch = new RowBatch();
        batch.Rows.Add(new(null, "subagent:native-child", RowKind.Subagent,
            JsonSerializer.SerializeToElement(payload, RowJsonContext.Rows.SubagentPayload), 1000));
        rows.Commit(info.SessionId, batch);
        return new("native", host.Fixture.Machine, info.SessionId, "subagent:native-child",
            rows.RowsAfter(info.SessionId, 0).Last().Seq, info.SessionId, new(host.Fixture.Machine, info.SessionId), 72);
    }

    private static void History(TwoHosts.Host host, string session)
    {
        var converter = new RowConverter(); var rows = RowStore.Open(host.Fixture.Db);
        rows.Commit(session, converter.Apply(new TurnStarted(1000, "source-derived-history", "Historical turn")));
        rows.Commit(session, converter.Apply(new TurnEnded(1001, "end_turn", null)));
    }

    private static void AssertIdentity(SessionOpenTargetRead read, SessionOpenTarget target, long generation, string? route)
    {
        Assert.Equal(target.Kind, read.Kind); Assert.Equal(target.Machine, read.Machine);
        Assert.Equal(generation, read.Generation); Assert.Equal(route, read.RouteMachine);
        Assert.Equal(new(target.Machine, target.Session), read.OriginalKey); Assert.Equal(target.RequestId, read.RequestId);
        Assert.Equal(new(target.Machine, generation, route), read.ExpectedSource);
    }
    private static SessionOpenTargetRead Result(JsonElement reply)
    {
        Assert.Equal("result", reply.GetProperty("t").GetString());
        return reply.GetProperty("result").Deserialize(SessionOpenJson.Default.SessionOpenTargetRead)!;
    }
    private static void AssertError(JsonElement reply, string code)
    {
        Assert.Equal("error", reply.GetProperty("t").GetString());
        Assert.Equal(code, reply.GetProperty("code").GetString());
        Assert.False(reply.TryGetProperty("result", out _));
    }
    private static async Task<ClientWebSocket> Open(Uri address, bool bulk = false)
    {
        var socket = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://{address.Authority}/socket{(bulk ? "/bulk" : "")}"), timeout.Token);
        await Send(socket, "{\"t\":\"hello\",\"token\":\"isolated-token\",\"protocol\":1}");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static async Task<JsonElement> Call(WebSocket socket, long id, SessionOpenTarget target)
    { await SendCall(socket, id, target); return await Read(socket); }
    private static Task SendCall(WebSocket socket, long id, SessionOpenTarget target) => Send(socket,
        $$"""{"t":"call","id":{{id}},"feature":"sessions","method":"open_target","args":{{JsonSerializer.Serialize(target, SessionOpenJson.Default.SessionOpenTarget)}}}""");
    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[65536];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); Assert.Equal(WebSocketMessageType.Text, part.MessageType); bytes.Write(buffer, 0, part.Count); }
        while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class OpenGate(ICallRegistry inner) : ICallRegistry
    {
        public bool OverridePeer; public CallPeer? Peer;
        public Action<JsonObject>? Mutate; public Func<CallContext, Task>? AfterReply; public Action? OnDispose;
        public int Requests; public int Disposals;
        public void Read<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result,
            Func<A, CallContext, Task<R>> handler, bool bulk = false) =>
            inner.Read(feature, method, args, SocketJson.Default.JsonElement, async (value, call) =>
            {
                Interlocked.Increment(ref Requests);
                var context = OverridePeer ? call with { Peer = Peer } : call;
                var resolved = await handler(value, context);
                if (context.SourceGuard is { } guard) call.SourceGuard = new TrackedGuard(guard, () => { Interlocked.Increment(ref Disposals); OnDispose?.Invoke(); });
                if (AfterReply is { } after) await after(call);
                var json = JsonSerializer.SerializeToNode(resolved, result)!.AsObject(); Mutate?.Invoke(json);
                using var document = JsonDocument.Parse(json.ToJsonString());
                return document.RootElement.Clone();
            }, bulk);
        public void Write<A, R>(string feature, string method, JsonTypeInfo<A> args, JsonTypeInfo<R> result, Func<A, CallContext, Task<Func<IWriteScope, R>>> handler,
            Func<A, string?>? order = null, Action<A, CallContext>? failed = null, bool bulk = false) => inner.Write(feature, method, args, result, handler, order, failed, bulk);
        public void Save<V>(string feature, JsonTypeInfo<V> value, Action<string, V, IWriteScope> save) => inner.Save(feature, value, save);
        public void Closed(Action<string> connection) => inner.Closed(connection);
        public void Topic<I>(string topic, JsonTypeInfo<I> item, Func<I, string> key, Func<string?, CallContext, IReadOnlyList<I>> state, bool terminal = false) => inner.Topic(topic, item, key, state, terminal);
        public void Rows(string topic, Func<string, long?, CallContext, (long Seq, IReadOnlyList<string> Rows)> state, Action<string, CallContext>? opened = null) => inner.Rows(topic, state, opened);
    }
    private sealed class TrackedGuard(IExpectedSourceGuard inner, Action disposed) : IExpectedSourceGuard
    {
        private int _disposed;
        public CancellationToken Changed => inner.Changed;
        public void EnsureCurrent() => inner.EnsureCurrent();
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { inner.Dispose(); disposed(); } }
    }
}
