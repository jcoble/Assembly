using Assembly.Application.Features.Terminal;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Terminals;
using System.Threading.Channels;
using Assembly.Tests.Draft.Terminal;

namespace Assembly.Tests.Features.Terminal;

[CollectionDefinition("Program terminal stderr", DisableParallelization = true)]
public sealed class ProgramTerminalCollection;

[Collection("Program terminal stderr")]
public sealed class TerminalProgramTests
{
    [Fact]
    public async Task A_sign_in_reader_failure_never_logs_its_raw_material()
    {
        const string secret = "READER_PRIVATE_CANARY";
        var previous = Console.Error;
        using var errors = new StringWriter();
        Console.SetError(errors);
        try
        {
            var pty = new FailedReader(secret);
            var info = new TerminalInfo("terminal", "/cwd", "/program", "/program", 96, 28, 42,
                "signin:operation", DateTimeOffset.UtcNow, null);
            await using (var terminal = new RunningTerminal(info, pty, new RecordingTopics()))
                await pty.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(secret, errors.ToString());
            Assert.True(pty.Disposed);
        }
        finally { Console.SetError(previous); }
    }

    private static ProgramStart Start(string owner = "signin:operation") =>
        new("/exact/program", ["literal argument", "$(not-a-shell)", "--last"],
            new Dictionary<string, string?> { ["PRIVATE_ENV"] = "value with spaces", ["REMOVE"] = null },
            "/exact/cwd", owner, 111, 37);

    [Fact]
    public async Task A_program_executes_exact_argv_env_cwd_and_size_without_any_session_lookup()
    {
        var starter = new FakePtyStarter();
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System,
            _ => throw new InvalidOperationException("must not look up a sign-in as a session"));
        IProgramTerminals programs = host;
        var start = Start();
        var terminal = await programs.StartProgramAsync(start, CancellationToken.None);
        var pty = Assert.Single(starter.Started);
        Assert.Equal(start.File, pty.Request.FileName);
        Assert.Equal(start.Args, pty.Request.Arguments);
        Assert.Equal(start.Env, pty.Request.Environment);
        Assert.Equal(start.Cwd, pty.Request.Cwd);
        Assert.Equal((111, 37), (pty.Request.Cols, pty.Request.Rows));
        Assert.Equal(pty.Pid, terminal.Pid);
        Assert.Equal(start.Owner, Assert.Single(host.List()).Owner);
        await programs.WriteAsync(terminal.Id, "CODE_PRIVATE_CANARY\r"u8.ToArray(), CancellationToken.None);
        Assert.Equal("CODE_PRIVATE_CANARY\r"u8.ToArray(), Assert.Single(pty.Written));
    }

    [Fact]
    public async Task A_program_is_neither_reused_nor_returned_as_the_owners_shell()
    {
        var starter = new FakePtyStarter();
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System);
        IProgramTerminals programs = host;
        var first = await programs.StartProgramAsync(Start(), CancellationToken.None);
        var second = await programs.StartProgramAsync(Start(), CancellationToken.None);
        var shell = await host.StartAsync(new StartArgs("/cwd", Owner: "signin:operation"));
        Assert.Equal(3, new[] { first.Id, second.Id, shell.Id }.Distinct().Count());
        Assert.Equal(["-l"], starter.Started[2].Request.Arguments);
    }

    [Fact]
    public async Task The_same_reader_pushes_output_and_publishes_the_last_bytes_before_exit()
    {
        var starter = new FakePtyStarter();
        var topics = new RecordingTopics();
        await using var host = new TerminalHost(starter, topics, TimeProvider.System);
        IProgramTerminals programs = host;
        var terminal = await programs.StartProgramAsync(Start(), CancellationToken.None);
        var output = Collect(programs.OutputAsync(terminal.Id, CancellationToken.None));
        var exit = programs.ExitedAsync(terminal.Id, CancellationToken.None);
        starter.Started[0].Emit("URL_PRIVATE_CANARY"u8.ToArray());
        starter.Started[0].Emit("last"u8.ToArray());
        starter.Started[0].End(7);
        Assert.Equal(7, await exit.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("URL_PRIVATE_CANARYlast"u8.ToArray(), await output.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("URL_PRIVATE_CANARYlast"u8.ToArray(), host.Read(terminal.Id, 0).Data);
        var rows = topics.Snapshot();
        Assert.Contains("\"exit\":7", rows[^1].Json);
        Assert.DoesNotContain("URL_PRIVATE_CANARY", rows[^1].Json);
    }

    [Fact]
    public async Task Cancelling_a_subscriber_or_exit_wait_does_not_cancel_the_program_or_another_owner()
    {
        var starter = new FakePtyStarter();
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System);
        IProgramTerminals programs = host;
        var first = await programs.StartProgramAsync(Start(), CancellationToken.None);
        var second = await programs.StartProgramAsync(Start("signin:other"), CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        var output = Collect(programs.OutputAsync(first.Id, cancelled.Token));
        var exit = programs.ExitedAsync(first.Id, cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => output);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exit);
        Assert.All(starter.Started, pty => Assert.False(pty.Disposed));
        await programs.CloseAsync(first.Id, CancellationToken.None);
        Assert.True(starter.Started[0].Disposed);
        Assert.False(starter.Started[1].Disposed);
        Assert.Equal(second.Id, Assert.Single(host.List()).Id);
        starter.Started[1].Emit("still running"u8.ToArray());
        starter.Started[1].End(0);
        Assert.Equal("still running"u8.ToArray(), await Collect(programs.OutputAsync(second.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task Closing_joins_the_pending_input_and_reader_and_removes_only_its_terminal()
    {
        var starter = new FakePtyStarter();
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System);
        IProgramTerminals programs = host;
        var terminal = await programs.StartProgramAsync(Start(), CancellationToken.None);
        var pty = starter.Started[0];
        pty.HoldWrites = true;
        var writing = programs.WriteAsync(terminal.Id, "CODE_PRIVATE_CANARY\r"u8.ToArray(), CancellationToken.None).AsTask();
        await pty.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await programs.CloseAsync(terminal.Id, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(writing.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.True(pty.Disposed);
        Assert.Empty(host.List());
        await programs.CloseAsync(terminal.Id, CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_during_a_start_disposes_the_late_pty_instead_of_registering_it()
    {
        using var cancelled = new CancellationTokenSource();
        var starter = new FakePtyStarter { BeforeStart = cancelled.Cancel };
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System);
        IProgramTerminals programs = host;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => programs.StartProgramAsync(Start(), cancelled.Token));
        Assert.True(Assert.Single(starter.Started).Disposed);
        Assert.Empty(host.List());
    }

    [Fact]
    public async Task Stopping_during_start_disposes_the_late_program_and_keeps_no_terminal()
    {
        var starter = new FakePtyStarter();
        await using var host = new TerminalHost(starter, new RecordingTopics(), TimeProvider.System);
        starter.BeforeStart = () => host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var error = await Assert.ThrowsAsync<CallFailed>(() => host.StartProgramAsync(Start(), CancellationToken.None));
        Assert.Equal("signin_start_failed", error.Message);
        Assert.True(Assert.Single(starter.Started).Disposed);
        Assert.Empty(host.List());
    }

    [Theory]
    [InlineData("start", "signin_start_failed")]
    [InlineData("read", "signin_read_failed")]
    [InlineData("write", "signin_write_failed")]
    [InlineData("exit", "signin_exit_failed")]
    public async Task Raw_failures_are_fixed_before_leaving_the_terminal_owner(string stage, string code)
    {
        const string secret = "FAILURE_PRIVATE_CANARY";
        var previous = Console.Error;
        using var errors = new StringWriter();
        Console.SetError(errors);
        var starter = new FaultStarter(stage, secret);
        var topics = new RecordingTopics();
        try
        {
            await using var host = new TerminalHost(starter, topics, TimeProvider.System);
            IProgramTerminals programs = host;
            CallFailed error;
            if (stage == "start")
                error = await Assert.ThrowsAsync<CallFailed>(() => programs.StartProgramAsync(Start(), CancellationToken.None));
            else
            {
                var terminal = await programs.StartProgramAsync(Start(), CancellationToken.None);
                if (stage == "write")
                    error = await Assert.ThrowsAsync<CallFailed>(async () => await programs.WriteAsync(terminal.Id, "CODE_PRIVATE_CANARY\r"u8.ToArray(), CancellationToken.None));
                else
                {
                    error = await Assert.ThrowsAsync<CallFailed>(() => programs.ExitedAsync(terminal.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
                    var outputError = await Assert.ThrowsAsync<CallFailed>(() => Collect(programs.OutputAsync(terminal.Id, CancellationToken.None)));
                    Assert.Equal(code, outputError.Reason);
                }
                await programs.CloseAsync(terminal.Id, CancellationToken.None);
                Assert.True(starter.Pty.Disposed);
            }
            Assert.Equal(code, error.Message);
            Assert.Equal(code, error.Reason);
            Assert.DoesNotContain(secret, errors.ToString());
            Assert.All(topics.Snapshot(), row => Assert.DoesNotContain(secret, row.Json));
        }
        finally { Console.SetError(previous); }
    }

    private static async Task<byte[]> Collect(IAsyncEnumerable<ReadOnlyMemory<byte>> output)
    {
        using var bytes = new MemoryStream();
        await foreach (var piece in output)
            bytes.Write(piece.Span);
        return bytes.ToArray();
    }

    private sealed class FaultStarter(string stage, string secret) : IPtyStarter
    {
        public FaultPty Pty { get; } = new(stage, secret);
        public IPty Start(PtyRequest request) => stage == "start" ? throw new IOException(secret) : Pty;
    }

    private sealed class FaultPty(string stage, string secret) : IPty
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        public int Pid => 43;
        public Task<int> Exited => stage == "exit" ? Task.FromException<int>(new IOException(secret)) : Task.FromResult(0);
        public bool Disposed { get; private set; }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (stage == "read") throw new IOException(secret);
            if (stage == "exit") return 0;
            await _output.Reader.WaitToReadAsync(cancellationToken);
            return 0;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new IOException(secret);
        public void Resize(int cols, int rows) { }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailedReader(string secret) : IPty
    {
        public int Pid => 42;
        public Task<int> Exited => Task.FromResult(1);
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ReadEntered.TrySetResult();
            throw new IOException(secret);
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void Resize(int cols, int rows) { }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
