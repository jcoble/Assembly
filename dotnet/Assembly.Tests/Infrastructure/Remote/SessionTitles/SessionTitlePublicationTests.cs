using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Infrastructure.Remote.SessionTitles;

public sealed class SessionTitlePublicationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Full_legacy_title_reconstructs_from_real_initial_state_or_change_in_both_forward_modes(bool forward, bool initial)
    {
        await using var h = new TwoHosts(forwardCommands: forward);
        await h.Start();
        var id = Guid.NewGuid().ToString();
        // A copied legacy row has no local owning Engine title: this fixed role must remain legacy-only.
        h.Remote.Sessions.Add(id, "codex", "repo", "another-peer");
        h.Mac.Sessions.Add(id, "codex", "repo", "workbox-test");
        h.Mac.Sessions.SetTitle(id, "old", "prompt");
        var title = new string('界', 1500000) + "\0FULL_TITLE😀";
        var copied = new TaskCompletionSource<SessionItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publication = h.Mac.Topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, json) =>
        {
            var item = JsonSerializer.Deserialize(json, SessionsJson.Default.SessionItem)!;
            if (item.SessionId == id && item.Title == title) copied.TrySetResult(item);
        });
        if (initial) h.Remote.Sessions.SetTitle(id, title, "prompt");
        await h.StartMirror();
        var binding = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Runtime.Machine, default);
        if (!initial) h.Remote.Sessions.SetTitle(id, title, "prompt");
        var received = await copied.Task.WaitAsync(Wait);
        Assert.Equal(title, received.Title);
        Assert.Equal(Encoding.UTF8.GetBytes(title), Encoding.UTF8.GetBytes(h.Mac.Sessions.Get(id)!.Title!));
        Assert.Equal("workbox-test", h.Mac.Sessions.Get(id)!.Machine);
        Assert.Equal(title, h.Remote.Sessions.Get(id)!.Title);
        Assert.Null(h.Remote.Fixture.Text("SELECT id FROM engine_session WHERE id='" + id + "'"));
        Assert.True(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(binding, default));
    }

    [Fact]
    public async Task Touch_before_every_actual_chunk_keeps_the_prefix_and_uses_latest_pushed_metadata()
    {
        await using var h = new TwoHosts();
        await h.Start();
        var source = h.Remote.Sessions.Create("codex", "repo");
        h.Mac.Sessions.Add(source.SessionId, "codex", "repo", "workbox-test");
        await h.StartMirror();
        var binding = await h.ConnectedMachine.CaptureOwnerBindingAsync(h.Remote.Runtime.Machine, default);
        var title = new string('x', 5 * 1024 * 1024) + "\0😀";
        var offsets = new List<long>();
        h.Remote.LegacyTitleReading = async (args, call) =>
        {
            offsets.Add(args.Offset);
            h.Remote.Fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
            h.Remote.Sessions.Touch(source.SessionId);
            // The same normal socket sends the pushed change before this response. Reading the response proves ingress.
            await h.ConnectedMachine.CallOwnerBindingAsync(binding, "machine", JsonSerializer.SerializeToElement(new { }), call.Cancellation);
        };
        var copied = new TaskCompletionSource<SessionItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publication = h.Mac.Topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, json) =>
        {
            var item = JsonSerializer.Deserialize(json, SessionsJson.Default.SessionItem)!;
            if (item.SessionId == source.SessionId && item.Title == title) copied.TrySetResult(item);
        });
        h.Remote.Sessions.SetTitle(source.SessionId, title, "prompt");
        var received = await copied.Task.WaitAsync(Wait);
        var length = Encoding.UTF8.GetByteCount(title);
        Assert.Equal(Enumerable.Range(0, (length + 1024 * 1024 - 1) / (1024 * 1024)).Select(i => (long)i * 1024 * 1024), offsets);
        Assert.Equal(h.Remote.Sessions.Get(source.SessionId)!.UpdatedAt, received.UpdatedAt);
        Assert.Equal(received.UpdatedAt, h.Mac.Sessions.Get(source.SessionId)!.UpdatedAt);
        Assert.True(await h.ConnectedMachine.IsCurrentOwnerBindingAsync(binding, default));
    }
}
