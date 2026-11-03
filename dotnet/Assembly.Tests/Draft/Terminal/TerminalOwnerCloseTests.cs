using Assembly.Application.Common.Terminals;
using Assembly.Application.Features.Terminal;

namespace Assembly.Tests.Draft.Terminal;

public sealed class TerminalOwnerCloseTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("assembly-terminal-close-").FullName;

    [Fact]
    public async Task Failed_concurrent_close_keeps_the_exact_terminal_retryable_until_successful_join()
    {
        var starter = new CloseStarter();
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System, ("/bin/sh", null), _ => _folder);
        var terminal = await host.StartAsync(new StartArgs(Owner: "a"));
        var pty = Assert.Single(starter.Items);
        pty.Fail = true;
        var first = host.CloseAsync(terminal.Id).AsTask();
        await pty.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = host.CloseAsync(terminal.Id).AsTask();
        Assert.Equal(terminal.Pid, Assert.Single(host.List()).Pid);
        pty.Release.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => second);
        Assert.Equal(1, pty.Attempts);
        Assert.Equal(terminal.Id, Assert.Single(host.List()).Id);
        pty.Fail = false;
        await host.CloseAsync(terminal.Id);
        Assert.Equal(2, pty.Attempts);
        Assert.Empty(host.List());
        await host.CloseAsync(terminal.Id);
    }

    [Fact]
    public async Task Exact_owner_close_includes_ended_utility_but_preserves_other_owner_and_direct_program()
    {
        var starter = new FakePtyStarter(); var topics = new RecordingTopics();
        await using var host = new TerminalHost(starter, topics, TimeProvider.System, ("/bin/sh", null), _ => _folder);
        var utility = await host.StartAsync(new StartArgs(Owner: "a"));
        starter.Started[0].End(0);
        await topics.WaitFor(row => row.Key == utility.Id && row.Json.Contains("\"exit\":"));
        var other = await host.StartAsync(new StartArgs(Owner: "b"));
        var program = await host.StartProgramAsync(new ProgramStart("/bin/sh", [], new Dictionary<string, string?>(), _folder, "signin:operation"), default);
        await host.CloseOwnerAsync("a");
        Assert.DoesNotContain(host.List(), item => item.Id == utility.Id);
        Assert.Contains(host.List(), item => item.Id == other.Id);
        Assert.Contains(host.List(), item => item.Id == program.Id);
        Assert.False(starter.Started[1].Disposed);
        Assert.False(starter.Started[2].Disposed);
    }

    [Fact]
    public async Task Shutdown_attempts_all_exact_terminals_and_preserves_a_failed_close_for_retry()
    {
        var starter = new CloseStarter();
        var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System, ("/bin/sh", null));
        var failed = await host.StartAsync(new StartArgs(_folder));
        await host.StartAsync(new StartArgs(_folder));
        starter.Items[0].Fail = true;
        foreach (var pty in starter.Items) pty.Release.TrySetResult();
        await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask());
        Assert.All(starter.Items, pty => Assert.Equal(1, pty.Attempts));
        Assert.Equal(failed.Id, Assert.Single(host.List()).Id);
        starter.Items[0].Fail = false;
        await host.DisposeAsync();
        Assert.Empty(host.List());
    }

    private sealed class CloseStarter : IPtyStarter
    {
        internal readonly List<ClosePty> Items = [];
        public IPty Start(PtyRequest request) { var pty = new ClosePty(12000 + Items.Count); Items.Add(pty); return pty; }
    }
    private sealed class ClosePty(int pid) : IPty
    {
        internal bool Fail; internal int Attempts;
        internal readonly TaskCompletionSource Closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Pid => pid;
        public Task<int> Exited => _exit.Task;
        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public void Resize(int cols, int rows) { }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Attempts); Closing.TrySetResult(); await Release.Task;
            if (Fail) throw new IOException("Controlled exact child close failure");
            _exit.TrySetResult(137);
        }
    }
    public void Dispose() => Directory.Delete(_folder, true);
}
