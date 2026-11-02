using System.Text.Json;
using Assembly.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class EngineForwardTests
{
    [Fact]
    public async Task Owner_disposal_joins_between_call_lease_and_waiting_admission_before_disposing_gates()
    {
        await using var h = new TwoHosts(windowCalls: true, forwardCommands: true);
        await h.Start(); await h.StartMirror();
        var remote = h.ConnectedMachine; var binding = await remote.CaptureOwnerBindingAsync(h.Remote.Fixture.Machine, default);
        var lease = await remote.AcquireRelayBulkAsync(binding, default);
        var args = JsonSerializer.Deserialize("""{"entityKind":"tool_call","entityId":"tool"}""", EngineJson.Default.JsonElement);
        await lease.CallAsync("entity", args, default);
        var waiting = remote.AcquireRelayBulkAsync(binding, default); Assert.False(waiting.IsCompleted);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = binding.Invalidated.Register(() => cancelled.TrySetResult());
        var disconnect = h.DisconnectMirror();
        try
        {
            await cancelled.Task.WaitAsync(Wait);
            Assert.IsNotType<ObjectDisposedException>(await Assert.ThrowsAnyAsync<Exception>(() => waiting));
            Assert.False(disconnect.IsCompleted);
            Assert.IsNotType<ObjectDisposedException>(await Assert.ThrowsAnyAsync<Exception>(() => remote.AcquireRelayBulkAsync(binding, default)));
            Assert.IsNotType<ObjectDisposedException>(await Assert.ThrowsAnyAsync<Exception>(() => lease.CallAsync("entity", args, default)));
            await lease.AbortAsync();
        }
        finally { await lease.DisposeAsync(); }
        await disconnect.WaitAsync(Wait);
    }

    [Fact]
    public async Task Relay_lease_holds_one_bulk_slot_across_all_parts_without_recursively_waiting()
    {
        await using var h = await Started();
        var remote = h.ConnectedMachine;
        var binding = await remote.CaptureOwnerBindingAsync(h.Remote.Fixture.Machine, default);
        var args = JsonSerializer.Deserialize("""{"entityKind":"tool_call","entityId":"tool"}""", EngineJson.Default.JsonElement);
        await using var lease = await remote.AcquireRelayBulkAsync(binding, default);
        Assert.Equal(JsonValueKind.Null, (await lease.CallAsync("entity", args, default)).ValueKind);
        var ordinary = remote.CallAsync("engine", "entity", args, true, default);
        Assert.False(ordinary.IsCompleted);
        Assert.Equal(JsonValueKind.Null, (await lease.CallAsync("entity", args, default)).ValueKind);
        Assert.Equal(2, h.Remote.OwnerReadCalls); Assert.False(ordinary.IsCompleted);
        await lease.DisposeAsync();
        Assert.Equal(JsonValueKind.Null, (await ordinary.WaitAsync(Wait)).ValueKind);
        Assert.Equal(3, h.Remote.OwnerReadCalls);
    }

    [Fact]
    public async Task Old_relay_lease_cannot_send_on_a_same_ID_replacement_normal_socket()
    {
        await using var h = await Started(); var remote = h.ConnectedMachine;
        var binding = await remote.CaptureOwnerBindingAsync(h.Remote.Fixture.Machine, default);
        var args = JsonSerializer.Deserialize("""{"entityKind":"tool_call","entityId":"tool"}""", EngineJson.Default.JsonElement);
        await using var lease = await remote.AcquireRelayBulkAsync(binding, default);
        await lease.CallAsync("entity", args, default);
        h.Remote.Endpoint.DropAll(); h.Remote.Commit(1); await h.Reached(h.Remote.Through);
        var reads = h.Remote.OwnerReadCalls;
        await Assert.ThrowsAnyAsync<Exception>(() => lease.CallAsync("entity", args, default));
        Assert.Equal(reads, h.Remote.OwnerReadCalls); Assert.True(binding.IsInvalidated);
        Assert.Equal(JsonValueKind.Object, (await remote.CallAsync("engine", "usage", JsonSerializer.Deserialize("""{"entityKind":"session","entityId":"s"}""", EngineJson.Default.JsonElement), false, default)).ValueKind);
    }

    [Fact]
    public async Task Failed_bulk_identity_does_not_abort_the_normal_owner_connection()
    {
        await using var h = new TwoHosts(windowCalls: true, forwardCommands: true);
        h.Remote.BulkIdentity = "different-owner"; await h.Start(); await h.StartMirror();
        var binding = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Fixture.Machine, default);
        await using var lease = await h.ConnectedMachine.AcquireRelayBulkAsync(binding, default);
        var args = JsonSerializer.Deserialize("""{"entityKind":"tool_call","entityId":"tool"}""", EngineJson.Default.JsonElement);
        await Assert.ThrowsAnyAsync<Exception>(() => lease.CallAsync("entity", args, default));
        Assert.Equal(0, h.Remote.OwnerReadCalls);
        Assert.True(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(binding, default));
    }
}
