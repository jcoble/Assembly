using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Remote.SessionTitles;

public sealed class SessionTitleLifecycleTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task New_inline_title_cancels_the_older_notice_before_apply_and_keeps_normal_owner()
    {
        await using var h = new TwoHosts(); await h.Start();
        var source = h.Remote.Sessions.Create("codex", "repo");
        h.Mac.Sessions.Add(source.SessionId, "codex", "repo", "workbox-test");
        h.Mac.Sessions.SetTitle(source.SessionId, "old", "prompt");
        await h.StartMirror();
        var owner = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Runtime.Machine, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Remote.LegacyTitleReading = async (_, call) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, call.Cancellation); }
            finally { cancelled.TrySetResult(); }
        };
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publication = h.Mac.Topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, json) =>
        {
            var item = JsonSerializer.Deserialize(json, SessionsJson.Default.SessionItem)!;
            if (item.SessionId == source.SessionId && item.Title == "new inline") copied.TrySetResult();
        });
        h.Remote.Sessions.SetTitle(source.SessionId, new string('x', 5 * 1024 * 1024), "prompt");
        await entered.Task.WaitAsync(Wait);
        h.Remote.Sessions.SetTitle(source.SessionId, "new inline", "prompt");
        await copied.Task.WaitAsync(Wait); await cancelled.Task.WaitAsync(Wait);
        Assert.True(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(owner, default));
        await h.StopMirror();
        Assert.Equal("new inline", h.Mac.Sessions.Get(source.SessionId)!.Title);
    }

    [Fact]
    public async Task A_true_large_rename_supersedes_old_content_without_an_old_completion_overwriting_it()
    {
        await using var h = new TwoHosts(); await h.Start();
        var source = h.Remote.Sessions.Create("codex", "repo");
        h.Mac.Sessions.Add(source.SessionId, "codex", "repo", "workbox-test");
        await h.StartMirror();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        h.Remote.LegacyTitleReading = async (_, call) =>
        {
            if (Interlocked.Increment(ref first) != 1) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, call.Cancellation); }
            finally { cancelled.TrySetResult(); }
        };
        var renamed = new string('y', 5 * 1024 * 1024) + "\0NEW😀";
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publication = h.Mac.Topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, json) =>
        {
            var item = JsonSerializer.Deserialize(json, SessionsJson.Default.SessionItem)!;
            if (item.SessionId == source.SessionId && item.Title == renamed) copied.TrySetResult();
        });
        h.Remote.Sessions.SetTitle(source.SessionId, new string('x', 5 * 1024 * 1024), "prompt");
        await entered.Task.WaitAsync(Wait);
        h.Remote.Sessions.SetTitle(source.SessionId, renamed, "prompt");
        await cancelled.Task.WaitAsync(Wait); await copied.Task.WaitAsync(Wait);
        await h.StopMirror();
        Assert.Equal(renamed, h.Mac.Sessions.Get(source.SessionId)!.Title);
    }

    [Fact]
    public async Task Disposal_cancels_and_joins_active_and_waiting_notice_operations_before_backing_resources()
    {
        await using var h = new TwoHosts(); await h.Start();
        var first = h.Remote.Sessions.Create("codex", "repo");
        var second = h.Remote.Sessions.Create("codex", "repo");
        foreach (var source in new[] { first, second })
        {
            h.Mac.Sessions.Add(source.SessionId, "codex", "repo", "workbox-test");
            h.Mac.Sessions.SetTitle(source.SessionId, "old", "prompt");
        }
        await h.StartMirror();
        var owner = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Runtime.Machine, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        h.Remote.LegacyTitleReading = async (_, call) =>
        {
            Interlocked.Increment(ref reads); entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, call.Cancellation); }
            finally { cancelled.TrySetResult(); }
        };
        var title = new string('x', 5 * 1024 * 1024);
        h.Remote.Sessions.SetTitle(first.SessionId, title, "prompt");
        await entered.Task.WaitAsync(Wait);
        h.Remote.Sessions.SetTitle(second.SessionId, title + "second", "prompt");
        // Same-socket response follows the second notice and proves it has entered the shared ingress queue.
        await h.ConnectedMachine.CallOwnerBindingAsync(owner, "machine", JsonSerializer.SerializeToElement(new { }), default);
        await h.StopMirror().WaitAsync(Wait); await cancelled.Task.WaitAsync(Wait);
        Assert.Equal(1, reads);
        Assert.Equal("old", h.Mac.Sessions.Get(first.SessionId)!.Title);
        Assert.Equal("old", h.Mac.Sessions.Get(second.SessionId)!.Title);
    }
}
