namespace Assembly.Tests.Infrastructure.Socket;

public sealed partial class SocketTests
{
    // A held write with an order key is still queued when the socket closes; the hook runs only once it has.
    [Fact]
    public async Task A_closed_sockets_hook_runs_after_every_write_it_queued()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = false;
        _calls.Read("notes", "wait", TestJson.Default.Note, TestJson.Default.Note, async (note, call) =>
        {
            waiting.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, call.Cancellation);
            }
            catch (OperationCanceledException)
            {
                closeSeen.SetResult();
                throw;
            }
            return note;
        });
        _calls.Write("notes", "held", TestJson.Default.Note, TestJson.Default.Note, async (note, _) =>
        {
            writing.SetResult();
            await gate.Task;
            return _ =>
            {
                written = true;
                return note;
            };
        }, note => note.Text);
        _calls.Closed(_ => hooked.TrySetResult(written));
        var socket = await OpenAsync();

        // The read goes first: a closing socket cancels in the reverse order the calls came, so once it's cancelled,
        // the backend is closing.
        await CallAsync(socket, 1, "notes", "wait", """{"text":"probe"}""");
        await waiting.Task.WaitAsync(Wait);
        await CallAsync(socket, 2, "notes", "held", """{"text":"key"}""");
        await writing.Task.WaitAsync(Wait);
        socket.Abort();
        socket.Dispose();
        await closeSeen.Task.WaitAsync(Wait);
        gate.SetResult();

        Assert.True(await hooked.Task.WaitAsync(Wait));
    }
}
