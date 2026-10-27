using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class UpdateRouterTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_closed_session_drops_what_came_before_its_resume_answer_and_keeps_what_came_after()
    {
        var all = Channel.CreateUnbounded<SessionNotification>();
        var router = new UpdateRouter(all.Reader);
        router.Forget("s1");

        all.Writer.TryWrite(Text("s1", "before"));
        all.Writer.TryWrite(SessionNotification.Resumed("s1"));
        all.Writer.TryWrite(Text("s1", "after"));
        router.Flush();

        var update = await router.For("s1").ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal("after", update.Update.GetProperty("content").GetProperty("text").GetString());
        Assert.False(router.For("s1").TryRead(out _));
    }

    [Fact]
    public void A_resume_answer_still_waiting_when_its_session_is_forgotten_doesnt_open_it_again()
    {
        var all = Channel.CreateUnbounded<SessionNotification>();
        var router = new UpdateRouter(all.Reader);

        // The answer came, then the opener failed and gave the session up before the router read the marker.
        all.Writer.TryWrite(SessionNotification.Resumed("s1"));
        router.Forget("s1");
        all.Writer.TryWrite(Text("s1", "stray"));
        router.Flush();

        Assert.False(router.For("s1").TryRead(out _));
    }

    private static SessionNotification Text(string sessionId, string text) =>
        new(sessionId, JsonDocument.Parse($$$"""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"{{{text}}}"}}""").RootElement);
}
