namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class SocketTests
{
    // A rows topic's opened callback runs once the sub is registered; if it throws, the sub is still the socket's, so an
    // unsub removes it, rather than leaving it in Topics for good.
    [Fact]
    public async Task A_sub_whose_opened_callback_throws_still_works_and_unsubscribes()
    {
        _calls.Rows("lines", (_, _, _) => (0, []), (_, _) => throw new InvalidOperationException("no session"));
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await SendAsync(socket, """{"t":"sub","id":1,"topic":"lines","key":"a"}""");
        Assert.Equal("""{"t":"state","sub":1,"seq":0,"items":[]}""", await ReadAsync(socket));
        await SendAsync(socket, """{"t":"unsub","id":1}""");
        // Frames are read in order, so once this answers, the unsub has been handled.
        await CallAsync(socket, 2, "notes", "echo", """{"text":"hi"}""");

        Assert.Equal("""{"t":"result","id":2,"result":{"text":"hi"}}""", await ReadAsync(socket));
        Assert.False(_topics.HasSubscribers("lines"));
    }
}
