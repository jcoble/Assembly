using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Machines;
using Assembly.Engine;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitCallReceiptTests
{
    [Fact]
    public async Task Cancellation_before_dispatch_sends_nothing_and_keeps_the_original_owner_usable()
    {
        await using var hosts = new TwoHosts();
        var calls = 0;
        hosts.Remote.AdditionalFeatures = registry => registry.Write("git", "start", EngineJson.Default.JsonElement,
            EngineJson.Default.JsonElement, (args, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<Func<IWriteScope, JsonElement>>(_ => args);
            });
        await hosts.Start(); await hosts.StartMirror();
        var machine = hosts.ConnectedMachine;
        var owner = await machine.CaptureOwnerReadAsync(null, default);
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        var args = JsonSerializer.SerializeToElement(new { operationId = Guid.NewGuid() });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => machine.CallGitWriteAsync("start", args, owner.Identity, owner.Socket, stopped.Token));
        Assert.Equal(0, calls);
        Assert.Equal(args.GetRawText(), (await machine.CallGitWriteAsync("start", args, owner.Identity, owner.Socket, default)).GetRawText());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_replaced_normal_owner_refuses_the_original_write_without_sending_it()
    {
        await using var hosts = new TwoHosts();
        var calls = 0;
        hosts.Remote.AdditionalFeatures = registry => registry.Write("git", "start", EngineJson.Default.JsonElement,
            EngineJson.Default.JsonElement, (args, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<Func<IWriteScope, JsonElement>>(_ => args);
            });
        await hosts.Start(); await hosts.StartMirror();
        var machine = hosts.ConnectedMachine;
        var owner = await machine.CaptureOwnerReadAsync(null, default);
        hosts.Remote.Endpoint.DropAll(); hosts.Remote.Commit(1);
        await hosts.Reached(hosts.Remote.Through);
        var args = JsonSerializer.SerializeToElement(new { operationId = Guid.NewGuid() });
        var error = await Assert.ThrowsAsync<MachineUnreachable>(() => machine.CallGitWriteAsync("start", args, owner.Identity, owner.Socket, default));
        Assert.False(error.MayHaveBeenSent); Assert.Equal(0, calls);
        var current = await machine.CaptureOwnerReadAsync(owner.Identity, default);
        Assert.NotSame(owner.Socket, current.Socket);
        await machine.CallGitWriteAsync("start", args, current.Identity, current.Socket, default);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("drop")]
    [InlineData("dispose")]
    public async Task Sent_cancel_or_lost_reply_is_uncertain_and_a_late_reply_does_not_complete_a_new_call(string mode)
    {
        await using var hosts = new TwoHosts();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        hosts.Remote.AdditionalFeatures = registry => registry.Write("git", "start", EngineJson.Default.JsonElement,
            EngineJson.Default.JsonElement, async (args, _) =>
            {
                if (Interlocked.Increment(ref count) == 1) { entered.TrySetResult(); await release.Task; }
                return _ => args;
            });
        await hosts.Start(); await hosts.StartMirror();
        var machine = hosts.ConnectedMachine;
        var owner = await machine.CaptureOwnerReadAsync(null, default);
        using var stop = new CancellationTokenSource();
        var first = JsonSerializer.SerializeToElement(new { operationId = "original" });
        var sent = machine.CallGitWriteAsync("start", first, owner.Identity, owner.Socket, stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (mode == "drop") hosts.Remote.Endpoint.DropAll();
            else if (mode == "dispose") await hosts.StopMirror().WaitAsync(TimeSpan.FromSeconds(15));
            else stop.Cancel();
            var failure = await Assert.ThrowsAsync<MachineUnreachable>(() => sent.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.True(failure.MayHaveBeenSent); Assert.Equal(1, count);
            release.TrySetResult();
            if (mode == "cancel")
            {
                var next = JsonSerializer.SerializeToElement(new { operationId = "different" });
                var result = await machine.CallGitWriteAsync("start", next, owner.Identity, owner.Socket, default);
                Assert.Equal("different", result.GetProperty("operationId").GetString());
                Assert.Equal(2, count);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Ordinary_call_cancellation_retains_its_existing_exception()
    {
        await using var hosts = new TwoHosts(windowCalls: true, forwardCommands: true);
        await hosts.Start(); await hosts.StartMirror();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hosts.Remote.OwnerReading = async (_, _) => { entered.TrySetResult(); await release.Task; };
        using var stop = new CancellationTokenSource();
        var args = JsonSerializer.SerializeToElement(new { entityKind = "session", entityId = "s" });
        var call = hosts.ConnectedMachine.CallAsync("engine", "usage", args, false, stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }
        finally { release.TrySetResult(); }
    }
}
