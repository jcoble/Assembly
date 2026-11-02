using System.Text.Json;
using Assembly.Domain.Events;
using Assembly.Application.Common.Acp;
using System.Threading.Channels;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Acp;

public sealed class ForegroundPublicationTests
{
    [Fact]
    public void Accepted_non_state_publishes_once_and_closed_stream_publishes_nothing()
    {
        using var foreground = new Foreground();
        var notification = JsonSerializer.Deserialize("""{"sessionId":"acp","update":{"sessionUpdate":"tool_call_update","toolCallId":"t"}}""", AcpJsonContext.Default.SessionNotification)!;
        var publications = 0;
        foreground.Read(notification, () => publications++);
        Assert.Equal(1, publications);
        foreground.Close(new IOException("closed"));
        foreground.Read(notification, () => publications++);
        Assert.Equal(1, publications);
    }

    [Fact]
    public async Task A_late_close_or_cancel_after_dispose_keeps_the_original_closed_outcome()
    {
        var foreground = new Foreground();
        foreground.Dispose();
        Assert.Null(Record.Exception(() => foreground.Close(new IOException("late pipe close"))));
        var cancelling = foreground.Cancelling();
        await Assert.ThrowsAnyAsync<Exception>(() => cancelling.Task);
        Assert.Null(Record.Exception(foreground.Dispose));
    }

    [Theory]
    [InlineData("end_turn")]
    [InlineData("cancelled")]
    public void Accepted_terminal_is_routable_before_prompt_or_cancel_completion(string reason)
    {
        using var foreground = new Foreground();
        var updates = Channel.CreateUnbounded<SessionNotification>();
        var router = new UpdateRouter(updates.Reader);
        var pending = foreground.Begin();
        foreground.Inserted(pending, "provider-message");
        var queue = router.For("acp");
        SessionNotification Notice(string json) => JsonSerializer.Deserialize(json, AcpJsonContext.Default.SessionNotification)!;
        foreground.Read(Notice("""{"sessionId":"acp","update":{"sessionUpdate":"user_message","messageId":"provider-message"}}"""));
        foreground.Read(Notice("""{"sessionId":"acp","update":{"sessionUpdate":"state_update","state":"running"}}"""));
        var cancelled = foreground.Cancelling();
        var terminal = Notice("""{"sessionId":"acp","update":{"sessionUpdate":"state_update","state":"idle","stopReason":"REASON","usage":{"costUsd":0.25}}}""".Replace("REASON", reason));
        var publications = 0;
        foreground.Read(terminal, () =>
        {
            Assert.False(pending.Ended.Task.IsCompleted);
            Assert.False(cancelled.Task.IsCompleted);
            Assert.True(updates.Writer.TryWrite(terminal)); publications++;
        });
        Assert.True(pending.Ended.Task.IsCompleted);
        if (reason == "cancelled") Assert.True(cancelled.Task.IsCompleted);
        router.Flush();
        Assert.True(queue.TryRead(out var saved));
        Assert.Same(terminal, saved);
        var events = new UpdateReader().Read(saved, 1000);
        Assert.IsType<TurnEnded>(events[0]);
        Assert.Equal(0.25m, Assert.IsType<UsageReported>(events[1]).CostUsd);
        Assert.Equal(1, publications);
        updates.Writer.TryComplete();
    }
}
