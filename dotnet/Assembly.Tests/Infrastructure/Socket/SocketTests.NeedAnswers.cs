using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Persistence;
using Assembly.Tests.Draft.Base.Chat;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class SocketTests
{
    [Fact]
    public async Task Answers_keep_the_session_order_key_while_other_sessions_can_answer()
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        using var hub = new LiveHub(RowStore.Open(db));
        var sessions = SessionStore.Open(db, _time, "checkouts");
        var first = sessions.Create("claude", "repo");
        var other = sessions.Create("claude", "repo");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = false;
        var door = new AnswerDoor();
        door.Answer = async request =>
        {
            if (request.Ask == "first")
            {
                reached.SetResult();
                await release.Task;
                return _ => { committed = true; return true; };
            }
            if (request.Ask == "second") Assert.True(committed);
            return _ => true;
        };
        _writer.Real = db.Writer;
        new SendFeature(sessions, new MachineSet(new FakeMachine(null, sessions)), hub, door).Register(_calls);
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "send", "answer", Args(first.SessionId, "first"));
        await reached.Task.WaitAsync(Wait);
        try
        {
            await CallAsync(socket, 2, "send", "answer", Args(first.SessionId, "second"));
            await CallAsync(socket, 3, "send", "answer", Args(other.SessionId, "other"));
            Assert.Equal("""{"t":"result","id":3,"result":{"ok":true}}""", await ReadAsync(socket));
        }
        finally { release.TrySetResult(); }
        var replies = await ReadByIdAsync(socket, 2);
        Assert.Equal("""{"t":"result","id":1,"result":{"ok":true}}""", replies[0]);
        Assert.Equal("""{"t":"result","id":2,"result":{"ok":true}}""", replies[1]);

        static string Args(string session, string ask) => $$$"""{"session":"{{{session}}}","ask":"{{{ask}}}","answer":{"optionId":"allow"}}""";
    }

    [Theory]
    [InlineData(true, """{"optionId":"allow"}""")]
    [InlineData(true, """{"action":"accept","content":{"name":"café","items":[1,{"nested":true}],"text":"line\u0000end"}}""")]
    [InlineData(false, """{"optionId":"allow"}""")]
    public async Task Send_answer_runs_the_prepared_closure_in_the_socket_writer(bool accepted, string answer)
    {
        using var file = new TempDatabase();
        using var db = TestDatabase.Open(file.Path);
        using var hub = new LiveHub(RowStore.Open(db));
        var sessions = SessionStore.Open(db, _time, "checkouts");
        var session = sessions.Create("claude", "repo");
        var door = new AnswerDoor();
        door.Answer = async request =>
        {
            Assert.Equal(session.SessionId, request.Session);
            Assert.Equal("ask-1", request.Ask);
            Assert.Equal(answer, request.Answer.GetRawText());
            await Task.Run(() => db.Writer.Write("prepare probe", _ => true)).WaitAsync(Wait);
            return scope =>
            {
                Assert.NotNull(scope);
                return accepted;
            };
        };
        _writer.Real = db.Writer;
        new SendFeature(sessions, new MachineSet(new FakeMachine(null, sessions)), hub, door).Register(_calls);
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "answer", $$$"""{"session":"{{{session.SessionId}}}","ask":"ask-1","answer":{{{answer}}}}""");

        var reply = await ReadAsync(socket);
        if (accepted) Assert.Equal("""{"t":"result","id":1,"result":{"ok":true}}""", reply);
        else
        {
            Assert.Equal("failed", Code(reply, 1));
            Assert.Contains("that ask isn't open, or the answer doesn't fit it", reply);
        }
        Assert.Equal(1, door.Prepared);
    }
}
