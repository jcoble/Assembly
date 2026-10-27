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

    // A write cancelled behind a running one with its key answers cancelled at once, and the next write with that key
    // still waits for the running one.
    [Fact]
    public async Task A_keyed_write_cancelled_while_queued_answers_at_once_and_the_next_still_waits_its_turn()
    {
        var gate = new TaskCompletionSource();
        var first = new TaskCompletionSource();
        _calls.Write("notes", "first", TestJson.Default.Note, TestJson.Default.Note, async (note, _) =>
        {
            first.SetResult();
            await gate.Task;
            return _ => note;
        }, note => note.Text);
        _calls.Write("notes", "later", TestJson.Default.Note, TestJson.Default.Note,
            (note, _) => Task.FromResult<Func<IWriteScope, Note>>(_ => note), note => note.Text);
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "first", """{"text":"key"}""");
        await first.Task.WaitAsync(Wait);
        await CallAsync(socket, 2, "notes", "later", """{"text":"key"}""");
        await SendAsync(socket, """{"t":"cancel","id":2}""");
        await CallAsync(socket, 3, "notes", "later", """{"text":"key"}""");
        string cancelled;
        try
        {
            cancelled = await ReadAsync(socket);
        }
        finally
        {
            gate.SetResult();
        }

        Assert.Equal("cancelled", Code(cancelled, id: 2));
        var answers = await ReadByIdAsync(socket, 2);
        Assert.Equal("""{"t":"result","id":1,"result":{"text":"key"}}""", answers[0]);
        Assert.Equal("""{"t":"result","id":3,"result":{"text":"key"}}""", answers[1]);
        Assert.Equal(["notes/first", "notes/later"], _writer.Written);
    }
}
