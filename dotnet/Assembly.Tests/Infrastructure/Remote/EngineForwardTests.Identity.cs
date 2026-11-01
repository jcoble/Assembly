using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Sessions;

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
        Assert.Equal("unreachable", error.GetProperty("code").GetString());
        Assert.Equal("workbox-test", error.GetProperty("machine").GetString());
        Assert.Equal(1, h.Remote.OwnerReadCalls); // No owner read payload passed the bulk identity fence.
        var args = JsonSerializer.Deserialize("""{"entityKind":"tool_call","entityId":"tool"}""", EngineJson.Default.JsonElement);
        var ordinary = await h.ConnectedMachine.CallAsync("engine", "entity", args, true, default);
        Assert.Equal(JsonValueKind.Null, ordinary.ValueKind);
        Assert.Equal(2, h.Remote.OwnerReadCalls);
        Assert.Equal(1, h.Remote.MachineCalls); // All forwarding reused the one mirror handshake.
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
