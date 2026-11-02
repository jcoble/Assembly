using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Infrastructure.Remote.SessionTitles;

public sealed class SessionTitlePhaseTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Verified_read_then_real_same_key_ingress_commits_latest_metadata_without_reading_the_body_again()
    {
        using var log = new GuardLog();
        await using var h = new TwoHosts(remoteLog: log);
        await h.Start();
        var source = h.Remote.Sessions.Create("codex", "repo");
        h.Mac.Sessions.Add(source.SessionId, "codex", "repo", "workbox-test");
        await h.StartMirror();
        var binding = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Runtime.Machine, default);
        var firstStat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stats = 0;
        var offsets = new List<long>();
        h.Remote.OwnerReading = async (method, call) =>
        {
            if (method == "legacy_session_title" && Interlocked.Increment(ref stats) == 1)
            {
                firstStat.TrySetResult();
                await release.Task.WaitAsync(call.Cancellation);
            }
        };
        h.Remote.LegacyTitleReading = (args, _) => { offsets.Add(args.Offset); return Task.CompletedTask; };
        var title = new string('界', 1500000) + "\0VERIFIED😀";
        var copied = new List<SessionItem>();
        using var publication = h.Mac.Topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, json) =>
        {
            var item = JsonSerializer.Deserialize(json, SessionsJson.Default.SessionItem)!;
            if (item.SessionId == source.SessionId && item.Title == title) copied.Add(item);
        });
        try
        {
            h.Remote.Sessions.SetTitle(source.SessionId, title, "prompt");
            await firstStat.Task.WaitAsync(Wait);
            // Controls the shared production PHASE order, not the automatic task's post-hash scheduler position.
            var verified = await h.ConnectedMachine.ReadVerifiedSessionTitleAsync(source.SessionId, default).WaitAsync(Wait);
            Assert.Empty(copied);
            var reads = offsets.ToArray();
            h.Remote.Fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
            h.Remote.Sessions.Touch(source.SessionId);
            // This normal response follows the pushed SAME-key change on the creating socket.
            await h.ConnectedMachine.CallOwnerBindingAsync(binding, "machine", JsonSerializer.SerializeToElement(new { }), default).WaitAsync(Wait);
            await h.ConnectedMachine.CommitVerifiedSessionTitleAsync(verified, default).WaitAsync(Wait);
            var committed = Assert.Single(copied);
            Assert.Equal(h.Remote.Sessions.Get(source.SessionId)!.UpdatedAt, committed.UpdatedAt);
            Assert.Equal(title, h.Mac.Sessions.Get(source.SessionId)!.Title);
            Assert.Equal(committed.UpdatedAt, h.Mac.Sessions.Get(source.SessionId)!.UpdatedAt);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.ConnectedMachine.CommitVerifiedSessionTitleAsync(verified, default));
            release.TrySetResult();
            await log.Rejected.Task.WaitAsync(Wait);
            // Disposal now joins the already-observed automatic publication rejection/cleanup.
            await h.StopMirror().WaitAsync(Wait);
            var length = Encoding.UTF8.GetByteCount(title);
            Assert.Equal(Enumerable.Range(0, (length + 1024 * 1024 - 1) / (1024 * 1024)).Select(i => (long)i * 1024 * 1024), offsets);
            Assert.Equal(reads, offsets);
            Assert.Equal(2, stats);
            Assert.Single(copied);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("inline")]
    [InlineData("content")]
    [InlineData("cancel")]
    [InlineData("owner")]
    [InlineData("replacement")]
    public async Task Old_verified_value_cannot_override_new_content_or_owner_and_cancellation_preserves_pending_work(string change)
    {
        await using var h = new TwoHosts();
        await h.Start();
        var source = h.Remote.Sessions.Create("codex", "repo");
        h.Mac.Sessions.Add(source.SessionId, "codex", "repo", "workbox-test");
        await h.StartMirror();
        var machine = h.ConnectedMachine;
        var binding = await machine.CaptureOwnerBindingAsync(h.Remote.Runtime.Machine, default);
        var firstStat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stats = 0;
        h.Remote.OwnerReading = async (method, call) =>
        {
            if (method == "legacy_session_title" && Interlocked.Increment(ref stats) == 1)
            {
                firstStat.TrySetResult();
                await release.Task.WaitAsync(call.Cancellation);
            }
        };
        var title = new string('x', 5 * 1024 * 1024) + "OLD\0😀";
        var newer = change == "inline" ? "new inline" : title + "NEW";
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publication = h.Mac.Topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, json) =>
        {
            var item = JsonSerializer.Deserialize(json, SessionsJson.Default.SessionItem)!;
            if (item.SessionId == source.SessionId && item.Title == newer) copied.TrySetResult();
        });
        try
        {
            h.Remote.Sessions.SetTitle(source.SessionId, title, "prompt");
            await firstStat.Task.WaitAsync(Wait);
            var verified = await machine.ReadVerifiedSessionTitleAsync(source.SessionId, default).WaitAsync(Wait);
            if (change == "cancel")
            {
                using var cancel = new CancellationTokenSource();
                await cancel.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => machine.CommitVerifiedSessionTitleAsync(verified, cancel.Token));
                await machine.CommitVerifiedSessionTitleAsync(verified, default).WaitAsync(Wait);
                Assert.Equal(title, h.Mac.Sessions.Get(source.SessionId)!.Title);
            }
            else if (change is "owner" or "replacement")
            {
                var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var observed = binding.Invalidated.Register(() => invalidated.TrySetResult());
                h.Remote.Endpoint.DropAll();
                await invalidated.Task.WaitAsync(Wait);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => machine.CommitVerifiedSessionTitleAsync(verified, default));
                Assert.NotEqual(title, h.Mac.Sessions.Get(source.SessionId)!.Title);
                release.TrySetResult();
                if (change == "replacement")
                {
                    await h.StopMirror().WaitAsync(Wait);
                    await h.StartMirror().WaitAsync(Wait);
                    var replacement = h.ConnectedMachine;
                    Assert.NotSame(machine, replacement);
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement.CommitVerifiedSessionTitleAsync(verified, default));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => machine.CommitVerifiedSessionTitleAsync(verified, default));
                }
            }
            else
            {
                h.Remote.Sessions.SetTitle(source.SessionId, newer, "prompt");
                await machine.CallOwnerBindingAsync(binding, "machine", JsonSerializer.SerializeToElement(new { }), default).WaitAsync(Wait);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => machine.CommitVerifiedSessionTitleAsync(verified, default));
                release.TrySetResult();
                await copied.Task.WaitAsync(Wait);
                Assert.Equal(newer, h.Mac.Sessions.Get(source.SessionId)!.Title);
                // The obsolete value must not clear the replacement operation after its own cleanup has run.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => machine.CommitVerifiedSessionTitleAsync(verified, default));
            }
            release.TrySetResult();
            await h.StopMirror().WaitAsync(Wait);
        }
        finally { release.TrySetResult(); }
    }


    private sealed class GuardLog : StringWriter
    {
        internal readonly TaskCompletionSource Rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains("session title pending (OperationCanceledException:", StringComparison.Ordinal) == true)
                Rejected.TrySetResult();
        }
    }

}
