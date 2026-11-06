using System.Text.Json;
using Assembly.Engine;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Remote;
using Assembly.Application.Common.Machines;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forwarding_waits_for_the_current_socket_identity_before_any_owner_payload(bool replace)
    {
        await using var h = await Started();
        var old = h.Remote.Fixture.Machine;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Identify() { entered.TrySetResult(); return release.Task; }
        if (replace)
        {
            await h.DisconnectMirror();
            await h.ReplaceAtSameEndpoint(new(windowCalls: true) { Identifying = Identify });
            await h.ReconnectSaved();
        }
        else
        {
            h.Remote.Identifying = Identify;
            h.Remote.Endpoint.DropAll();
        }
        try
        {
            await entered.Task.WaitAsync(Wait);
            var receipts = h.Remote.Fixture.Count("engine_command_receipt");
            var through = h.Remote.Through;
            using var live = await Open(h.Mac);
            using var bulk = await Open(h.Mac, true);
            await Send(live, Command(1, old, Guid.NewGuid(), new StartSession("codex", "model", "/test", "Wrong peer", null, null, null), entity: "misrouted"));
            var command = await Read(live);
            Assert.Equal("error", command.GetProperty("t").GetString());
            Assert.Equal("unreachable", command.GetProperty("code").GetString());
            await Send(live, Call(2, "usage", "session", "s", old));
            Assert.Equal("unreachable", (await Read(live)).GetProperty("code").GetString());
            await Send(bulk, Call(3, "entity", "tool_call", "tool", old));
            Assert.Equal("unreachable", (await Read(bulk)).GetProperty("code").GetString());
            Assert.Equal(0, h.Remote.OwnerReadCalls);
            Assert.Equal(receipts, h.Remote.Fixture.Count("engine_command_receipt"));
            Assert.Equal(through, h.Remote.Through);
            Assert.Null(h.Remote.Fixture.State("engine_session", "misrouted"));
            if (!replace) h.Remote.Commit(1); // A new completion proves this reconnect's handshake finished.
            release.TrySetResult();
            await h.Reached(h.Remote.Through);
            if (replace)
            {
                await Send(live, Command(4, old, Guid.NewGuid(), new StartSession("codex", "model", "/test", "Old", null, null, null), entity: "old"));
                Assert.Equal("not_found", (await Read(live)).GetProperty("code").GetString());
            }
            await Send(live, Command(5, h.Remote.Fixture.Machine, Guid.NewGuid(), new StartSession("codex", "model", "/test", "Current", null, null, null), entity: "current"));
            Assert.True(Result(await Read(live)).Applied);
            Assert.Equal(h.Remote.Fixture.Machine, h.Remote.Fixture.Text("SELECT machine FROM engine_session WHERE id='current'"));
            await Send(live, Call(6, "usage", "session", "s", h.Remote.Fixture.Machine));
            Assert.Equal("result", (await Read(live)).GetProperty("t").GetString());
            await Send(bulk, Call(7, "entity", "tool_call", "tool", h.Remote.Fixture.Machine));
            Assert.Equal("result", (await Read(bulk)).GetProperty("t").GetString());
            Assert.Equal(replace ? 1 : 2, h.Remote.MachineCalls);
        }
        finally { release.TrySetResult(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forwarded_bulk_reads_require_a_matching_engine_welcome_but_ordinary_calls_keep_legacy_behavior(bool omitted)
    {
        await using var h = new TwoHosts(windowCalls: true, forwardCommands: true);
        h.Remote.OmitBulkIdentity = omitted;
        h.Remote.BulkIdentity = "different-engine";
        await h.Start(); await h.StartMirror();
        using var live = await Open(h.Mac);
        using var bulk = await Open(h.Mac, true);
        var machine = h.Remote.Fixture.Machine;
        await Send(live, Call(1, "usage", "session", "s", machine));
        Assert.Equal("result", (await Read(live)).GetProperty("t").GetString());
        Assert.Equal(1, h.Remote.OwnerReadCalls);
        await Send(bulk, Call(2, "entity", "tool_call", "tool", machine));
        var error = await Read(bulk);
        Assert.Equal(omitted ? "unreachable" : "engine_differs", error.GetProperty("code").GetString());
        Assert.Equal(omitted ? "workbox-test is reconnecting" : "workbox-test is now a different machine from the one this belongs to", error.GetProperty("message").GetString());
        Assert.Equal("workbox-test", error.GetProperty("machine").GetString());
        Assert.Equal(1, h.Remote.OwnerReadCalls); // No owner read payload passed the bulk identity fence.
        var args = JsonSerializer.Deserialize("""{"entityKind":"tool_call","entityId":"tool"}""", EngineJson.Default.JsonElement);
        var ordinary = await h.ConnectedMachine.CallAsync("engine", "entity", args, true, default);
        Assert.Equal(JsonValueKind.Null, ordinary.ValueKind);
        Assert.Equal(2, h.Remote.OwnerReadCalls);
        Assert.Equal(1, h.Remote.MachineCalls); // All forwarding reused the one mirror handshake.
    }

    [Fact]
    public async Task While_a_machine_reconnects_a_call_only_waits_and_it_isnt_called_a_different_machine()
    {
        await using var h = await Started();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.Identifying = () => { entered.TrySetResult(); return release.Task; };
        h.Remote.Endpoint.DropAll();
        try
        {
            await entered.Task.WaitAsync(Wait);
            using var live = await Open(h.Mac);
            await Send(live, Call(1, "usage", "session", "s", h.Remote.Fixture.Machine));
            var error = await Read(live);
            Assert.Equal("unreachable", error.GetProperty("code").GetString());
            Assert.Equal("workbox-test is reconnecting", error.GetProperty("message").GetString());
            Assert.Equal(0, h.Remote.OwnerReadCalls);
        }
        finally { release.TrySetResult(); }
    }

    private static RemoteMachine OnFakeClock(TwoHosts h, Microsoft.Extensions.Time.Testing.FakeTimeProvider clock, LiveHub hub) =>
        new("returning", h.Remote.Address, "isolated-token", hub, SessionStore.Open(h.Mac.Fixture.Db, h.Mac.Fixture.Time, "/isolated"), TextWriter.Null,
            new MachineStatus(h.Mac.Topics, []), topics: h.Mac.Topics, time: clock);

    /// <summary>Moves the fake clock until the machine opens its socket again, and says how long that took on that clock.</summary>
    private static async Task<TimeSpan> NextOpen(TwoHosts h, Microsoft.Extensions.Time.Testing.FakeTimeProvider clock, System.Threading.Channels.Channel<DateTimeOffset> opens, DateTimeOffset since, bool dropNew)
    {
        using var limit = new CancellationTokenSource(Wait);
        while (true)
        {
            if (opens.Reader.TryRead(out var at)) return at - since;
            if (dropNew) h.Remote.Endpoint.DropAll();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.WhenAny(opens.Reader.WaitToReadAsync().AsTask(), Task.Delay(5)).WaitAsync(limit.Token);
        }
    }

    [Fact]
    public async Task A_remote_that_connects_and_drops_is_tried_less_and_less_often()
    {
        await using var h = await Started();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var hub = new LiveHub(RowStore.Open(h.Mac.Fixture.Db));
        await using var machine = OnFakeClock(h, clock, hub);
        var opens = System.Threading.Channels.Channel.CreateUnbounded<DateTimeOffset>();
        machine.SocketOpened += () => opens.Writer.TryWrite(clock.GetUtcNow());
        await machine.ProbeAsync(default);
        var last = await opens.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        var gap = TimeSpan.Zero;
        for (var flap = 0; flap < 5; flap++)
        {
            gap = await NextOpen(h, clock, opens, last, dropNew: true);
            last += gap;
        }
        // Each of these lived no time at all. Resetting on connect kept this at about 1 s for good.
        Assert.True(gap >= TimeSpan.FromSeconds(4), $"still retrying after {gap}");
    }

    [Fact]
    public async Task A_lost_socket_that_had_proved_itself_comes_back_quickly_however_many_times()
    {
        await using var h = await Started();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var hub = new LiveHub(RowStore.Open(h.Mac.Fixture.Db));
        await using var machine = OnFakeClock(h, clock, hub);
        var opens = System.Threading.Channels.Channel.CreateUnbounded<DateTimeOffset>();
        machine.SocketOpened += () => opens.Writer.TryWrite(clock.GetUtcNow());
        await machine.ProbeAsync(default);
        await opens.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        for (var loss = 1; loss <= 5; loss++)
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            var lost = clock.GetUtcNow();
            h.Remote.Endpoint.DropAll();
            var back = await NextOpen(h, clock, opens, lost, dropNew: false);
            // A backoff that kept growing would be at 5 s by now.
            Assert.True(back < TimeSpan.FromSeconds(4), $"loss {loss} took {back}");
        }
    }

    [Fact]
    public async Task A_tunnel_coming_back_cuts_the_wait_short()
    {
        await using var h = await Started();
        var never = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var sessions = SessionStore.Open(h.Mac.Fixture.Db, h.Mac.Fixture.Time, "/isolated");
        using var hub = new LiveHub(RowStore.Open(h.Mac.Fixture.Db));
        await using var machine = new RemoteMachine("returning", h.Remote.Address, "isolated-token", hub, sessions, TextWriter.Null,
            new MachineStatus(h.Mac.Topics, []), topics: h.Mac.Topics, time: never);
        var opens = System.Threading.Channels.Channel.CreateUnbounded<int>();
        machine.SocketOpened += () => opens.Writer.TryWrite(1);
        await machine.ProbeAsync(default);
        await opens.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        h.Remote.Endpoint.DropAll();
        // The fake clock never lets the backoff end, so only the wake can bring it back.
        while (!opens.Reader.TryRead(out _))
        {
            machine.ConnectNow();
            await Task.WhenAny(opens.Reader.WaitToReadAsync().AsTask(), Task.Delay(50)).WaitAsync(Wait);
        }
    }

    [Fact]
    public async Task A_live_reconnect_during_bulk_open_cannot_release_an_old_payload()
    {
        await using var h = await Started();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.OpeningBulk = () => { opening.TrySetResult(); return release.Task; };
        using var bulk = await Open(h.Mac, true);
        using var live = await Open(h.Mac);
        try
        {
            await Send(bulk, Call(1, "entity", "tool_call", "tool", h.Remote.Fixture.Machine));
            await opening.Task.WaitAsync(Wait);
            h.Remote.Endpoint.DropAll();
            h.Remote.Commit(1);
            await h.Reached(h.Remote.Through); // Same Engine ID, positively verified on a different live socket.
            release.TrySetResult();
            Assert.Equal("unreachable", (await Read(bulk)).GetProperty("code").GetString());
            Assert.Equal(0, h.Remote.OwnerReadCalls);
            await Send(live, Call(2, "usage", "session", "s", h.Remote.Fixture.Machine));
            Assert.Equal("result", (await Read(live)).GetProperty("t").GetString());
            h.Remote.OpeningBulk = null;
            await Send(bulk, Call(3, "entity", "tool_call", "tool", h.Remote.Fixture.Machine));
            Assert.Equal("result", (await Read(bulk)).GetProperty("t").GetString());
            Assert.Equal(2, h.Remote.OwnerReadCalls);
            Assert.Equal(2, h.Remote.MachineCalls);
        }
        finally { release.TrySetResult(); }
    }

}
