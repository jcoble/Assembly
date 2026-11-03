using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed class SessionOpenSourcesTests
{
    [Fact]
    public async Task Explicit_local_null_returns_the_actual_engine_with_no_saved_route_lookup_and_closed_arguments()
    {
        await using var hosts = new TwoHosts();
        ICallRegistry calls = null!;
        hosts.Mac.AdditionalFeatures = registry => calls = registry;
        await hosts.Start();
        var db = new ObservedDatabase(hosts.Mac.Fixture.Db);
        Register(hosts, calls, db);
        using var socket = await Open(hosts.Mac.Address);
        var local = Result(await Call(socket, 1, "{\"routeMachine\":null}"));
        Assert.Equal(new ExpectedSource(hosts.Mac.Runtime.Machine, 0, null), local);
        foreach (var invalid in new[] { "{}", "{\"routeMachine\":null,\"machine\":\"forged\"}", "{\"routeMachine\":1}" })
            AssertError(await Call(socket, 2, invalid), "bad_args");
        AssertError(await Call(socket, 3, "{\"routeMachine\":\"\"}"), "source_changed");
        Assert.Equal(0, db.Snapshots); Assert.Equal(0, db.Reads);
    }

    [Fact]
    public async Task Nonowners_and_cancelled_or_closed_callers_refuse_before_any_route_lookup()
    {
        await using var hosts = new TwoHosts(); await hosts.Start();
        var db = new ObservedDatabase(hosts.Mac.Fixture.Db);
        var sources = new SessionOpenSources(db, hosts.Mac.Runtime, null);
        foreach (var peer in new CallPeer?[] { null, new PhonePeer("paired-device") })
        {
            var error = await Assert.ThrowsAsync<CallFailed>(() => sources.ResolveAsync(new("saved-route"), new(default, "test", Peer: peer)));
            Assert.Equal("forbidden", error.Reason);
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sources.ResolveAsync(new("saved-route"), new(cancelled.Token, "test", Peer: new OwnerPeer())));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sources.ResolveAsync(new("saved-route"), new(default, "test", Closed: cancelled.Token, Peer: new OwnerPeer())));
        Assert.Equal(0, db.Snapshots); Assert.Equal(0, db.Reads);
    }

    [Fact]
    public async Task Saved_route_is_resolved_in_one_snapshot_to_the_actual_current_owner_generation()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        ICallRegistry calls = null!;
        hosts.Mac.AdditionalFeatures = registry => calls = registry;
        await hosts.Start(); await hosts.StartMirror(); await Ready(hosts);
        var db = new ObservedDatabase(hosts.Mac.Fixture.Db);
        Register(hosts, calls, db, configured: true);
        using var socket = await Open(hosts.Mac.Address);
        var read = Result(await Call(socket, 1, "{\"routeMachine\":\"workbox-test\"}"));
        Assert.Equal(Current(hosts), read);
        Assert.NotEqual("workbox-test", read.Machine);
        Assert.Equal(1, db.Snapshots);
        AssertError(await Call(socket, 2, "{\"routeMachine\":\"unknown-saved-route\"}"), "source_changed");
        AssertError(await Call(socket, 3, JsonSerializer.Serialize(new { routeMachine = hosts.Remote.Fixture.Machine })), "source_changed");
    }

    [Fact]
    public async Task Ambiguous_and_retired_saved_routes_refuse_instead_of_selecting_a_first_or_cached_owner()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        await hosts.Start(); await hosts.StartMirror(); await Ready(hosts);
        var db = hosts.Mac.Fixture.Db;
        var sources = new SessionOpenSources(db, hosts.Mac.Runtime, hosts.Connections);
        // Explicit invalid persisted metadata is a negative fixture, never a positive connection witness.
        db.Writer.Write("test.ambiguous-route", scope => scope.Execute("""
            INSERT INTO engine_mirror(machine,name,generation,through,retained_after,state,updated_ms)
            SELECT 'unconnected-duplicate',name,generation,through,retained_after,'live',updated_ms
            FROM engine_mirror WHERE machine=$machine
            """, new SqlArg("$machine", hosts.Remote.Fixture.Machine)));
        Assert.Equal("source_changed", (await Assert.ThrowsAsync<CallFailed>(() => sources.ResolveAsync(new("workbox-test"), Owner()))).Reason);
        db.Writer.Write("test.retired-route", scope =>
        {
            scope.Execute("DELETE FROM engine_mirror WHERE machine='unconnected-duplicate'");
            return scope.Execute("UPDATE engine_mirror SET state='retired' WHERE machine=$machine", new SqlArg("$machine", hosts.Remote.Fixture.Machine));
        });
        Assert.Equal("source_changed", (await Assert.ThrowsAsync<CallFailed>(() => sources.ResolveAsync(new("workbox-test"), Owner()))).Reason);
    }

    [Fact]
    public async Task Offline_route_refuses_and_an_already_resolved_descriptor_is_invalidated_by_disconnect()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        await hosts.Start(); await hosts.StartMirror(); await Ready(hosts);
        var sources = new SessionOpenSources(hosts.Mac.Fixture.Db, hosts.Mac.Runtime, hosts.Connections);
        var call = Owner();
        var source = await sources.ResolveAsync(new("workbox-test"), call);
        Assert.Equal(Current(hosts), source);
        using var guard = call.SourceGuard!; guard.EnsureCurrent();
        await hosts.DisconnectMirror();
        Assert.True(guard.Changed.IsCancellationRequested);
        Assert.Equal("source_changed", Assert.Throws<CallFailed>(guard.EnsureCurrent).Reason);
        Assert.Equal("source_changed", (await Assert.ThrowsAsync<CallFailed>(() => sources.ResolveAsync(new("workbox-test"), Owner()))).Reason);
    }

    [Fact]
    public async Task Same_saved_route_replacement_discovers_only_the_new_actual_engine_and_revokes_the_old_reply_guard()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        await hosts.Start(); await hosts.StartMirror(); await Ready(hosts);
        var sources = new SessionOpenSources(hosts.Mac.Fixture.Db, hosts.Mac.Runtime, hosts.Connections);
        var oldCall = Owner();
        var old = await sources.ResolveAsync(new("workbox-test"), oldCall);
        using var oldGuard = oldCall.SourceGuard!;
        var mirror = hosts.Connections.CaptureMirrorOwners().Single().Mirror;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaughtUp() => changed.TrySetResult();
        mirror.CaughtUp += CaughtUp;
        try
        {
            await hosts.ReplaceAtSameEndpoint(new TwoHosts.Host());
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { mirror.CaughtUp -= CaughtUp; }
        Assert.Equal("source_changed", Assert.Throws<CallFailed>(oldGuard.EnsureCurrent).Reason);
        var currentCall = Owner();
        var current = await sources.ResolveAsync(new("workbox-test"), currentCall);
        using var currentGuard = currentCall.SourceGuard!; currentGuard.EnsureCurrent();
        Assert.NotEqual(old.Machine, current.Machine);
        Assert.Equal("workbox-test", current.RouteMachine);
        Assert.Equal(Current(hosts), current);
    }

    private static CallContext Owner() => new(default, "test", Peer: new OwnerPeer());
    private static ExpectedSource Current(TwoHosts hosts) => new(hosts.Remote.Fixture.Machine,
        hosts.Mac.Fixture.Number("SELECT generation FROM engine_mirror WHERE machine=$machine AND state='live'",
            new SqlArg("$machine", hosts.Remote.Fixture.Machine)), "workbox-test");
    private static void Register(TwoHosts hosts, ICallRegistry calls, IAppDatabase db, bool configured = false) =>
        new SessionOpenCalls(db, hosts.Mac.Runtime, hosts.Mac.Sessions,
            new MachineSet(new FakeMachine(null, hosts.Mac.Sessions)), configured ? hosts.Connections : null).Register(calls);
    private static async Task Ready(TwoHosts hosts)
    {
        var mirror = hosts.Connections.CaptureMirrorOwners().Single().Mirror;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaughtUp() => ready.TrySetResult();
        mirror.CaughtUp += CaughtUp;
        try
        {
            if (await mirror.CaptureLiveBindingAsync(default) is not null) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { mirror.CaughtUp -= CaughtUp; }
    }
    private static ExpectedSource Result(JsonElement frame)
    {
        Assert.Equal("result", frame.GetProperty("t").GetString());
        var result = frame.GetProperty("result");
        Assert.Equal(new[] { "generation", "machine", "routeMachine" }, result.EnumerateObject().Select(property => property.Name).Order().ToArray());
        return result.Deserialize(SessionOpenJson.Default.ExpectedSource)!;
    }
    private static void AssertError(JsonElement frame, string code)
    {
        Assert.Equal("error", frame.GetProperty("t").GetString()); Assert.Equal(code, frame.GetProperty("code").GetString());
        Assert.False(frame.TryGetProperty("result", out _));
    }
    private static async Task<ClientWebSocket> Open(Uri address)
    {
        var socket = new ClientWebSocket(); await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/socket" }.Uri, default);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"hello\",\"protocol\":1,\"token\":\"isolated-token\"}"), WebSocketMessageType.Text, true, default);
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static async Task<JsonElement> Call(WebSocket socket, int id, string args)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes($$"""{"t":"call","id":{{id}},"feature":"sessions","method":"resolve_source","args":{{args}}}"""), WebSocketMessageType.Text, true, default);
        return await Read(socket);
    }
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var data = new MemoryStream();
        var bytes = new byte[8192]; WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), stop.Token); Assert.Equal(WebSocketMessageType.Text, part.MessageType); data.Write(bytes, 0, part.Count); } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(data.ToArray()); return json.RootElement.Clone();
    }
    private sealed class ObservedDatabase(IAppDatabase inner) : IAppDatabase
    {
        internal int Snapshots, Reads;
        public IAtomicWriter Writer => inner.Writer;
        public IReadSnapshot OpenSnapshot(CancellationToken ct) { Snapshots++; return inner.OpenSnapshot(ct); }
        public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken ct, params SqlArg[] args) { Reads++; return inner.Query(sql, map, ct, args); }
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken ct, params SqlArg[] args) { Reads++; return inner.QueryFirst(sql, map, ct, args); }
    }
}
