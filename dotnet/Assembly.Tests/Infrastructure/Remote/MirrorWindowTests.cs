using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Mirror;
using Assembly.Engine.Execution;
using Assembly.Engine.Contracts;
using Assembly.Engine.Reading;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class MirrorWindowTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Window_gets_live_pages_and_keeps_local_and_remote_cursors_separate()
    {
        await using var h = new TwoHosts(windowCalls: true);
        await h.Start();
        h.Mac.Fixture.SeedSession();
        await h.StartMirror();
        using var socket = await Open(h.Mac);
        await Subscribe(socket, 1, h.Remote.Fixture.Machine);
        var snapshot = await Read(socket);
        Assert.Equal(h.Remote.Fixture.Machine, snapshot.GetProperty("machine").GetString());
        Assert.Equal("s", Assert.Single(snapshot.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        await Subscribe(socket, 2, h.Mac.Fixture.Machine);
        Assert.Equal(h.Mac.Fixture.Machine, (await Read(socket)).GetProperty("machine").GetString());
        h.Remote.Commit(1, "foreign");
        var remote = await Read(socket);
        Assert.Equal(1, remote.GetProperty("id").GetInt64());
        Assert.Equal(h.Remote.Fixture.Machine, remote.GetProperty("machine").GetString());
        Assert.Equal(1, Assert.Single(remote.GetProperty("events").EnumerateArray()).GetProperty("seq").GetInt64());
        Assert.Equal("session.changed", remote.GetProperty("at").GetProperty("kind").GetString());
        h.Mac.Commit(1, "local");
        var local = await Read(socket);
        Assert.Equal(2, local.GetProperty("id").GetInt64());
        Assert.Equal(h.Mac.Fixture.Machine, local.GetProperty("machine").GetString());
        Assert.Equal("local", h.Mac.Runtime.Reader.Entity("session", "s", default)!.Value.GetProperty("title").GetString());
        Assert.Equal("foreign", h.Mac.Runtime.Reader.Entity("session", "s", default, h.Remote.Fixture.Machine)!.Value.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Window_cursor_on_a_mirror_survives_mac_restart()
    {
        await using var h = new TwoHosts(windowCalls: true);
        await h.Start(); await h.StartMirror();
        h.Remote.Commit(2); await h.Reached(2);
        var at = h.Mac.Runtime.Reader.EventAt(2, default, h.Remote.Fixture.Machine);
        await h.StopMac(); h.Remote.Commit(3); await h.StartMac();
        using var socket = await Open(h.Mac);
        await Send(socket, $$$"""{"t":"sub","id":1,"stream":"engine","topic":"sessions","machine":"{{{h.Remote.Fixture.Machine}}}","after":2,"at":{"kind":"{{{at!.Kind}}}","entityId":"{{{at.EntityId}}}","version":{{{at.Version}}}}}""");
        var frame = await Read(socket);
        Assert.Equal("engine_events", frame.GetProperty("t").GetString());
        Assert.Equal(new long[] { 3, 4, 5 }, frame.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()));
    }

    [Fact]
    public async Task Bulk_entity_reads_current_remote_generation_and_filters_snapshot_keys_in_sql()
    {
        await using var h = new TwoHosts(windowCalls: true); await h.Start();
        await h.StartMirror(); h.Remote.Commit(1, "remote title"); await h.Reached(1);
        using var socket = await Open(h.Mac, true);
        await Send(socket, $$$"""{"t":"call","id":1,"feature":"engine","method":"entity","args":{"entityKind":"session","entityId":"s","machine":"{{{h.Remote.Fixture.Machine}}}"}}""");
        Assert.Equal("remote title", (await Read(socket)).GetProperty("result").GetProperty("title").GetString());
        using var selected = h.Mac.Runtime.Reader.Snapshot("sessions", "s", default, h.Remote.Fixture.Machine);
        Assert.Single(selected.ReadNext().Items);
        using var missing = h.Mac.Runtime.Reader.Snapshot("sessions", "other", default, h.Remote.Fixture.Machine);
        Assert.Empty(missing.ReadNext().Items);
        Assert.Null(h.Mac.Runtime.Reader.Entity("tool_call", "s", default, h.Remote.Fixture.Machine));
    }

    [Fact]
    public async Task Remote_resnapshot_resets_existing_window_subscription()
    {
        await using var h = new TwoHosts(windowCalls: true); await h.Start(); await h.StartMirror();
        h.Remote.Commit(3); await h.Reached(3);
        using var socket = await Open(h.Mac); await Subscribe(socket, 1, h.Remote.Fixture.Machine); await Read(socket);
        await h.StopMirror(); h.Remote.Commit(4, "replacement");
        h.Remote.Fixture.Db.Writer.Write("test.compact", s => s.Execute("DELETE FROM engine_event WHERE seq<7; UPDATE engine_meta SET retained_after=6"));
        await h.StartMirror();
        var frame = await Read(socket);
        Assert.Equal("engine_snapshot", frame.GetProperty("t").GetString());
        Assert.Equal(7, frame.GetProperty("through").GetInt64());
        Assert.Equal("replacement", Assert.Single(frame.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal("session.changed", frame.GetProperty("at").GetProperty("kind").GetString());
        h.Remote.Commit(1); Assert.Equal(8, (await Read(socket)).GetProperty("through").GetInt64());
    }

    [Fact]
    public async Task Unknown_retired_and_explicit_local_machine_are_distinct()
    {
        await using var h = new TwoHosts(windowCalls: true); await h.Start(); await h.StartMirror();
        var old = h.Remote.Fixture.Machine;
        await h.StopMirror(); await h.ReplaceRemote(); await h.StartMirror();
        using var socket = await Open(h.Mac);
        foreach (var machine in new[] { "unknown", old })
        {
            await Subscribe(socket, 1, machine);
            Assert.Equal("not_found", (await Read(socket)).GetProperty("code").GetString());
        }
        await Subscribe(socket, 2, h.Mac.Fixture.Machine);
        var local = await Read(socket);
        Assert.Equal("engine_snapshot", local.GetProperty("t").GetString());
        Assert.Equal(h.Mac.Fixture.Machine, local.GetProperty("machine").GetString());
        Assert.Throws<KeyNotFoundException>(() => h.Mac.Runtime.Reader.HighWatermark(default, old));
    }

    [Fact]
    public async Task Mirror_retention_is_bounded_by_its_own_window_ack()
    {
        await using var h = new TwoHosts(windowCalls: true); await h.Start(); await h.StartMirror();
        using var socket = await Open(h.Mac); await Subscribe(socket, 1, h.Remote.Fixture.Machine); await Read(socket);
        // Stay within the existing 16-frame machine-link queue. Wait for actual consumption of
        // each burst; a giant live burst intentionally closes that bounded link and re-snapshots.
        for (var cut = 10; cut <= 11000; cut += 10)
        {
            h.Remote.Commit(10);
            long through = cut - 10;
            while (through < cut)
            {
                var frame = await Read(socket);
                Assert.Equal("engine_events", frame.GetProperty("t").GetString());
                through = frame.GetProperty("through").GetInt64();
            }
        }
        Assert.Equal(0, h.Mac.Fixture.Number("SELECT retained_after FROM engine_mirror WHERE machine=$m", new SqlArg("$m", h.Remote.Fixture.Machine)));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Retained()
        {
            if (h.Mac.Fixture.Number("SELECT retained_after FROM engine_mirror WHERE machine=$m", new SqlArg("$m", h.Remote.Fixture.Machine)) == 700) done.TrySetResult();
        }
        h.Mac.RetentionCommitted += Retained;
        try
        {
            await Send(socket, """{"t":"ack","id":1,"stream":"engine","after":700}""");
            await done.Task.WaitAsync(Wait);
            Assert.Equal(700, h.Mac.Fixture.Number("SELECT retained_after FROM engine_mirror WHERE machine=$m", new SqlArg("$m", h.Remote.Fixture.Machine)));
            Assert.Equal(10300, h.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_event WHERE machine=$m", new SqlArg("$m", h.Remote.Fixture.Machine)));
            Assert.Equal(0, h.Mac.Fixture.Number("SELECT retained_after FROM engine_meta"));
        }
        finally { h.Mac.RetentionCommitted -= Retained; }
    }

    [Fact]
    public async Task Remote_cursor_fingerprints_and_unmirrored_reads_use_the_selected_machine()
    {
        await using var h = new TwoHosts(windowCalls: true); await h.Start(); await h.StartMirror();
        h.Remote.Commit(2); await h.Reached(2);
        using var socket = await Open(h.Mac);
        await Send(socket, $$$"""{"t":"sub","id":1,"stream":"engine","topic":"sessions","machine":"{{{h.Remote.Fixture.Machine}}}","after":2,"at":{"kind":"other.history","entityId":"s","version":2}}""");
        Assert.Equal("bad_args", (await Read(socket)).GetProperty("code").GetString());
        await Send(socket, $$$"""{"t":"sub","id":1,"stream":"engine","topic":"sessions","machine":"{{{h.Remote.Fixture.Machine}}}","after":3}""");
        Assert.Equal("bad_args", (await Read(socket)).GetProperty("code").GetString());
        await Send(socket, $$$"""{"t":"call","id":2,"feature":"engine","method":"usage","args":{"entityKind":"session","entityId":"s","machine":"{{{h.Remote.Fixture.Machine}}}"}}""");
        Assert.Equal("not_found", (await Read(socket)).GetProperty("code").GetString());
        using var bulk = await Open(h.Mac, true);
        await Send(bulk, """{"t":"call","id":3,"feature":"engine","method":"entity","args":{"entityKind":"session","entityId":"s","machine":"unknown"}}""");
        Assert.Equal("not_found", (await Read(bulk)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mirrored_snapshot_pages_stay_on_one_generation_and_apply_relation_keys()
    {
        await using var h = new TwoHosts(windowCalls: true); await h.Start();
        h.Remote.Fixture.SeedRun(); h.Remote.Fixture.SeedStep();
        h.Remote.Fixture.SeedSession("child", parentId: "s", runId: "r");
        for (var i = 0; i < 210; i++) h.Remote.Fixture.SeedSession($"page-{i:D3}");
        await h.StartMirror();
        using var selected = h.Mac.Runtime.Reader.Snapshot("runs", "r", default, h.Remote.Fixture.Machine);
        Assert.Equal(new[] { "run", "step" }, selected.ReadNext().Items.Select(i => i.GetProperty("entityKind").GetString()));
        using var snapshot = h.Mac.Runtime.Reader.Snapshot("sessions", null, default, h.Remote.Fixture.Machine);
        var first = snapshot.ReadNext(); Assert.True(first.HasMore); Assert.InRange(first.Items.Count, 1, 200);
        using var socket = await Open(h.Mac);
        await Send(socket, $$$"""{"t":"sub","id":1,"stream":"engine","topic":"mirror","machine":"{{{h.Remote.Fixture.Machine}}}"}""");
        Assert.True((await Read(socket)).GetProperty("more").GetBoolean());
        // Promotion while the SQLite read transaction is open cannot mix the second page's generation.
        h.Mac.Fixture.Db.Writer.Write("test.replace", s =>
        {
            MirrorApply.BeginSnapshot(s, h.Remote.Fixture.Machine, 1000);
            return MirrorApply.EndSnapshot(s, h.Remote.Fixture.Machine, 0, null, 1000);
        });
        // A reset also interrupts a partial pulled snapshot without requiring the obsolete page's next.
        var reset = await Read(socket);
        Assert.Equal("engine_snapshot", reset.GetProperty("t").GetString());
        Assert.Empty(reset.GetProperty("items").EnumerateArray());
        Assert.False(reset.GetProperty("more").GetBoolean());
        var last = snapshot.ReadNext(); Assert.False(last.HasMore); Assert.Equal(212 - first.Items.Count, last.Items.Count);
        Assert.Equal(212, first.Items.Concat(last.Items).Select(i => i.GetProperty("id").GetString()).Distinct().Count());
        using var current = h.Mac.Runtime.Reader.Snapshot("sessions", null, default, h.Remote.Fixture.Machine);
        Assert.Empty(current.ReadNext().Items);
    }

    [Fact]
    public async Task A_publisher_cannot_drain_replacement_history_before_its_generation_reset()
    {
        await using var h = new TwoHosts(windowCalls: true); await h.Start(); await h.StartMirror();
        var machine = h.Remote.Fixture.Machine;
        var generation = h.Mac.Fixture.Number("SELECT generation FROM engine_mirror");
        var publisher = new EnginePublisher(h.Mac.Fixture.Db, machine, generation: generation);
        List<EngineEvent> events = [];
        using var subscription = publisher.Register(events.Add, out _);
        h.Mac.Fixture.Db.Writer.Write("test.replace-history", s =>
        {
            MirrorApply.BeginSnapshot(s, machine, 1000);
            MirrorApply.EndSnapshot(s, machine, 0, null, 1000);
            return MirrorApply.Events(s, machine, 0, 1, new("replacement", "s", 1),
                [new(machine, 1, "replacement", "session", "s", 1, 1000, false, new("s", null, null, null, null, null))], [], 1000);
        });
        publisher.Drain(default);
        Assert.Empty(events);
        publisher.Reset(0, generation + 1);
        publisher.Drain(default);
        Assert.Equal("replacement", Assert.Single(events).Kind);
    }

    private static Task Subscribe(WebSocket socket, int id, string machine) => Send(socket,
        $$$"""{"t":"sub","id":{{{id}}},"stream":"engine","topic":"sessions","machine":"{{{machine}}}"}""");
    private static async Task<ClientWebSocket> Open(TwoHosts.Host host, bool bulk = false)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new UriBuilder(host.Address) { Scheme = "ws", Path = bulk ? "/socket/bulk" : "/socket" }.Uri, default);
        await Send(socket, """{"t":"hello","token":"isolated-token","protocol":1}""");
        Assert.Equal("welcome", (await Read(socket)).GetProperty("t").GetString()); return socket;
    }
    private static Task Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(Wait); using var buffer = new MemoryStream();
        var bytes = new byte[65536]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(bytes, timeout.Token); Assert.Equal(WebSocketMessageType.Text, result.MessageType); buffer.Write(bytes, 0, result.Count); } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(buffer.ToArray()); return json.RootElement.Clone();
    }
}
