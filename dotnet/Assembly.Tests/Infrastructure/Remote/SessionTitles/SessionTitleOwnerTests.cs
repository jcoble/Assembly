using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Remote.SessionTitles;

public sealed class SessionTitleOwnerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Fixed_bulk_content_refusal_keeps_the_exact_normal_owner_alive()
    {
        await using var h = new TwoHosts(); await h.Start(); await h.StartMirror();
        var source = h.Remote.Sessions.Create("codex", "repo");
        h.Remote.Sessions.SetTitle(source.SessionId, "original", "prompt");
        var reference = Reference(h, source.SessionId, "original");
        h.Remote.Sessions.SetTitle(source.SessionId, "changed", "prompt");
        var owner = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Runtime.Machine, default);
        var error = await Assert.ThrowsAsync<CallFailed>(() => h.ConnectedMachine.CallAsync("engine", "legacy_session_title_chunk",
            JsonSerializer.SerializeToElement(new LegacySessionTitleChunkArgs(reference, 0, 1024), SessionsJson.Default.LegacySessionTitleChunkArgs),
            true, default, h.Remote.Runtime.Machine, expectedOwner: owner));
        Assert.Equal("title_changed", error.Message);
        Assert.True(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(owner, default));
        await h.ConnectedMachine.CallOwnerBindingAsync(owner, "machine", JsonSerializer.SerializeToElement(new { }), default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fixed_bulk_read_cancellation_or_normal_owner_loss_never_returns_old_content(bool disconnect)
    {
        await using var h = new TwoHosts(); await h.Start(); await h.StartMirror();
        var source = h.Remote.Sessions.Create("codex", "repo"); h.Remote.Sessions.SetTitle(source.SessionId, "original", "prompt");
        var owner = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Runtime.Machine, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.LegacyTitleReading = async (_, call) => { entered.TrySetResult(); await release.Task.WaitAsync(call.Cancellation); };
        using var cancellation = new CancellationTokenSource();
        var read = h.ConnectedMachine.CallAsync("engine", "legacy_session_title_chunk", JsonSerializer.SerializeToElement(
            new LegacySessionTitleChunkArgs(Reference(h, source.SessionId, "original"), 0, 1024), SessionsJson.Default.LegacySessionTitleChunkArgs),
            true, cancellation.Token, h.Remote.Runtime.Machine, expectedOwner: owner);
        try
        {
            await entered.Task.WaitAsync(Wait);
            if (disconnect)
            {
                var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var observed = owner.Invalidated.Register(() => invalidated.TrySetResult());
                h.Remote.Endpoint.DropAll(); await invalidated.Task.WaitAsync(Wait);
            }
            else await cancellation.CancelAsync();
        }
        finally { release.TrySetResult(); }
        var error = await Record.ExceptionAsync(async () => await read.WaitAsync(Wait));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        if (disconnect) Assert.False(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(owner, default));
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    private static LegacySessionTitleRef Reference(TwoHosts h, string id, string title)
    {
        var source = h.Remote.Sessions.Get(id)!;
        var bytes = Encoding.UTF8.GetBytes(title);
        return new(id, h.Remote.Runtime.Machine, source.Machine, source.ParentSessionId, source.TitleSource,
            source.UpdatedAt, bytes.LongLength, SessionTitleHash.Compute(bytes));
    }
}
