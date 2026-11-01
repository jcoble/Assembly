using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Remote.Smoke;

namespace Assembly.Tests.Acp;

public sealed class RecordWatcherTests
{
    private static SessionNotification Update(string json) => new("s", JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async Task Wait_handles_events_before_and_after_registration_and_drains_the_queue()
    {
        var channel = Channel.CreateUnbounded<SessionNotification>();
        var watcher = new UpdateWatcher(channel.Reader);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var turn = watcher.BeginTurn();
        var waiting = watcher.WaitForAsync(update => update.Kind == "state_update", deadline.Token);
        await channel.Writer.WriteAsync(Update("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hello"}}"""));
        await channel.Writer.WriteAsync(Update("""{"sessionUpdate":"state_update","state":"idle"}"""));
        await waiting;
        await watcher.DrainAsync();
        Assert.Equal("hello", turn.Text);
        Assert.True((await watcher.WaitForAsync(update => update.Kind == "agent_message_chunk", deadline.Token)).Kind == "agent_message_chunk");
        channel.Writer.Complete();
        await watcher.Completion.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task Cancellation_removes_a_wait_and_stream_close_settles_remaining_waits()
    {
        var channel = Channel.CreateUnbounded<SessionNotification>();
        var watcher = new UpdateWatcher(channel.Reader);
        using var cancelled = new CancellationTokenSource();
        var wait = watcher.WaitForAsync(_ => false, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        var closing = watcher.WaitForAsync(_ => false, default);
        channel.Writer.Complete();
        await Assert.ThrowsAsync<IOException>(() => closing.WaitAsync(TimeSpan.FromSeconds(5)));
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => watcher.WaitForAsync(_ => false, default));
    }

    [Fact]
    public async Task Replacement_text_signals_first_text_and_replaces_the_current_turn()
    {
        var turn = new TurnLog();
        turn.Add(Update("""{"sessionUpdate":"agent_message","messageId":"m","content":[{"type":"text","text":"whole"}]}"""));
        await turn.FirstText.WaitAsync(TimeSpan.FromSeconds(5));
        turn.Add(Update("""{"sessionUpdate":"agent_message_chunk","messageId":"m","content":{"type":"text","text":" later"}}"""));
        Assert.Equal("whole later", turn.Text);
        turn.Add(Update("""{"sessionUpdate":"agent_message","messageId":"m","content":[]}"""));
        Assert.Equal("", turn.Text);
    }
}
