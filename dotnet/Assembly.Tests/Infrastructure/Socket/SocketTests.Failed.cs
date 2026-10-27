using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class SocketTests
{
    // Whatever a write answers instead of its result, an error from its database part included, its failed callback
    // runs, so a feature can let go of what the write opened.
    [Fact]
    public async Task A_writes_failed_callback_runs_when_it_answers_anything_but_its_result()
    {
        var failed = new List<string>();
        _calls.Write("notes", "bad", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult<Func<IWriteScope, Note>>(scope =>
        {
            scope.Execute("INSERT INTO nowhere(id) VALUES (1);");
            return note;
        }), failed: (note, _) => failed.Add(note.Text));
        _calls.Write("notes", "good", TestJson.Default.Note, TestJson.Default.Note, (note, _) => Task.FromResult<Func<IWriteScope, Note>>(_ => note),
            failed: (note, _) => failed.Add(note.Text));
        var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "bad", """{"text":"bad"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
        await CallAsync(socket, 2, "notes", "good", """{"text":"good"}""");
        Assert.Contains("\"t\":\"result\"", await ReadAsync(socket));

        Assert.Equal(["bad"], failed);
    }

    // A failed callback that throws is logged, and the call still answers its own error.
    [Fact]
    public async Task A_failed_callback_that_throws_leaves_the_calls_own_error()
    {
        _calls.Write("notes", "bad", TestJson.Default.Note, TestJson.Default.Note,
            (Func<Note, CallContext, Task<Func<IWriteScope, Note>>>)((_, _) => throw new CallFailed("the real reason")),
            failed: (_, _) => throw new IOException("cleanup broke"));
        var socket = await OpenAsync();

        await CallAsync(socket, 1, "notes", "bad", """{"text":"bad"}""");

        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"the real reason"}""", await ReadAsync(socket));
    }
}
