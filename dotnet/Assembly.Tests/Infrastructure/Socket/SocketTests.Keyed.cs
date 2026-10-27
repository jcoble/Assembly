using Assembly.Application.Common.Database;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class SocketTests
{
    // A keyed write's handler can do work before its first await (a store read, starting an adapter). That runs off
    // the receive loop, so the frames behind it are still read.
    [Fact]
    public async Task A_keyed_write_that_blocks_before_its_first_await_doesnt_hold_up_a_read()
    {
        using var gate = new ManualResetEventSlim();
        _calls.Write("notes", "blocking", TestJson.Default.Note, TestJson.Default.Note, (note, _) =>
        {
            gate.Wait(Wait);
            return Task.FromResult<Func<IWriteScope, Note>>(_ => note);
        }, note => note.Text);
        _calls.Read("notes", "echo", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult(note));
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "blocking", """{"text":"key"}""");
        await CallAsync(socket, 2, "notes", "echo", """{"text":"hi"}""");

        try
        {
            Assert.Equal("""{"t":"result","id":2,"result":{"text":"hi"}}""", await ReadAsync(socket));
        }
        finally
        {
            gate.Set();
        }
        Assert.Equal("""{"t":"result","id":1,"result":{"text":"key"}}""", await ReadAsync(socket));
    }
}
