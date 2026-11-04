using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Terminals;
using Assembly.Application.Features.Agents;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Agents;
using Assembly.Tests.Core.Acp;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class SignInRunnerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private const string AgentMethod = """[{"methodId":"actual","name":"Actual","type":"agent"}]""";
    private const string TerminalMethod = """[{"methodId":"cli","name":"CLI","type":"terminal","args":["login","literal arg"],"env":[{"name":"METHOD","value":"fresh"}]}]""";
    private static AgentDescriptor Descriptor(IReadOnlyDictionary<string, string>? env = null) =>
        new("provider", "Provider", "/configured/program", ["base", "$(literal)"], env ?? new Dictionary<string, string> { ["CAPTURED"] = "one" });

    [Fact]
    public async Task Start_returns_the_handle_without_launching_until_Completion_is_first_observed()
    {
        var launcher = new Launcher();
        var runner = Runner(launcher);
        await using var session = await runner.StartAsync(Descriptor(), null, "operation", default);
        Assert.Equal("operation", session.OperationId);
        Assert.Equal(0, launcher.LaunchCount);
        var first = session.Completion;
        Assert.Same(first, session.Completion);
        var link = await launcher.Next();
        await Initialize(link, AgentMethod);
        var login = await link.Agent.ReadAsync();
        Assert.Equal("auth/login", Method(login));
        await link.Agent.ReplyAsync(login, "{}");
        Assert.Equal(SignInEnd.SignedIn, await first.WaitAsync(Limit));
        Assert.True(link.Disposed);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task Cancel_before_Completion_initializes_nothing_and_completion_never_faults()
    {
        var launcher = new Launcher();
        await using var session = await Runner(launcher).StartAsync(Descriptor(), null, "operation", default);
        await session.CancelAsync();
        Assert.Equal(SignInEnd.Cancelled, await session.Completion.WaitAsync(Limit));
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task Cancelling_an_inflight_initialize_joins_the_exact_dedicated_link()
    {
        var launcher = new Launcher();
        await using var session = await Runner(launcher).StartAsync(Descriptor(), null, "operation", default);
        var work = session.Completion;
        var link = await launcher.Next();
        Assert.Equal("initialize", Method(await link.Agent.ReadAsync()));
        await session.CancelAsync().WaitAsync(Limit);
        Assert.True(link.Disposed);
        Assert.Equal(SignInEnd.Cancelled, await work);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task Cancellation_waits_for_a_late_owned_launch_and_closes_it_without_initializing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new Launcher { ReleaseLaunch = gate };
        await using var session = await Runner(launcher).StartAsync(Descriptor(), null, "operation", default);
        var work = session.Completion;
        var link = await launcher.Next();
        var cancel = session.CancelAsync();
        Assert.False(cancel.IsCompleted);
        gate.TrySetResult();
        await cancel.WaitAsync(Limit);
        Assert.True(link.Disposed);
        Assert.Equal(0, link.Writes.Count);
        Assert.Equal(SignInEnd.Cancelled, await work);
    }

    [Fact]
    public async Task Explicit_missing_method_refuses_the_fresh_initialize_catalogue_without_guessing()
    {
        var launcher = new Launcher();
        await using var session = await Runner(launcher).StartAsync(Descriptor(), "stale", "operation", default);
        var work = session.Completion;
        var link = await launcher.Next();
        await Initialize(link, AgentMethod);
        Assert.Equal(SignInEnd.Refused, await work.WaitAsync(Limit));
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task Url_consent_has_one_generation_and_waits_for_the_exact_original_click_ack()
    {
        var board = new ChallengeBoard();
        var changed = Channel.CreateUnbounded<string>();
        board.Changed += id => changed.Writer.TryWrite(id);
        var launcher = new Launcher();
        await using var session = await Runner(launcher, board: board).StartAsync(Descriptor(), "actual", "operation", default);
        var work = session.Completion;
        var link = await launcher.Next();
        await Initialize(link, AgentMethod);
        var login = await link.Agent.ReadAsync();
        const string original = "https://example.invalid/%2f?PRIVATE_URL_CANARY=one%2Btwo";
        await link.Agent.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":991,\"method\":\"elicitation/create\",\"params\":{\"mode\":\"url\",\"url\":" + JsonSerializer.Serialize(original) + ",\"elicitationId\":\"url\",\"requestId\":" + login.GetProperty("id").GetRawText() + "}}");
        await changed.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        var challenge = Assert.IsType<Challenge>(board.Current("operation"));
        Assert.Equal(original, challenge.Link!.OriginalString);
        Assert.False(changed.Reader.TryRead(out _), "presentation must not post a competing generation");
        Assert.False(work.IsCompleted);
        await board.AcknowledgeUrlAsync("operation", challenge.Generation, original, default);
        var answer = await link.Agent.ReadAsync();
        Assert.Equal("accept", answer.GetProperty("result").GetProperty("action").GetString());
        await link.Agent.ReplyAsync(login, "{}");
        Assert.Equal(SignInEnd.SignedIn, await work.WaitAsync(Limit));
        Assert.Null(board.Current("operation"));
    }

    [Fact]
    public async Task Terminal_uses_captured_env_and_appended_fresh_args_then_verifies_on_another_new_link()
    {
        var sourceEnv = new Dictionary<string, string> { ["CAPTURED"] = "one" };
        var launcher = new Launcher { CanRunTerminalAuthentication = true };
        var terminals = new Terminals();
        await using var session = await Runner(launcher, terminals).StartAsync(Descriptor(sourceEnv), "cli", "operation", default);
        sourceEnv["CAPTURED"] = "late mutation";
        var work = session.Completion;
        var discovery = await launcher.Next();
        await Initialize(discovery, TerminalMethod);
        var start = await terminals.Started.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        Assert.True(discovery.Disposed);
        Assert.Equal("/configured/program", start.File);
        Assert.Equal(["base", "$(literal)", "login", "literal arg"], start.Args);
        Assert.Equal("one", start.Env["CAPTURED"]);
        Assert.Equal("fresh", start.Env["METHOD"]);
        Assert.Equal("signin:operation", start.Owner);
        Assert.Equal("one", launcher.Captured[0].Environment["CAPTURED"]);
        terminals.End(0);
        var verification = await launcher.Next();
        Assert.NotSame(discovery, verification);
        await Initialize(verification, TerminalMethod);
        var probe = await verification.Agent.ReadAsync();
        Assert.Equal("session/new", Method(probe));
        Assert.Equal("/probe", probe.GetProperty("params").GetProperty("cwd").GetString());
        await verification.Agent.ReplyAsync(probe, """{"sessionId":"disposable-probe","configOptions":[{"id":"model","category":"model","type":"select","currentValue":"actual","options":[{"value":"actual","name":"Actual"}]}]}""");
        var close = await verification.Agent.ReadAsync();
        Assert.Equal("session/close", Method(close));
        await verification.Agent.ReplyAsync(close, "{}");
        Assert.Equal(SignInEnd.SignedIn, await work.WaitAsync(Limit));
        Assert.Equal(1, terminals.CloseCount);
        Assert.True(verification.Disposed);
        Assert.Equal(2, launcher.LaunchCount);
    }

    [Fact]
    public async Task A_terminal_failure_does_not_verify_or_manufacture_login_success()
    {
        var launcher = new Launcher { CanRunTerminalAuthentication = true };
        var terminals = new Terminals();
        await using var session = await Runner(launcher, terminals).StartAsync(Descriptor(), null, "operation", default);
        var work = session.Completion;
        await Initialize(await launcher.Next(), TerminalMethod);
        await terminals.Started.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        terminals.End(3);
        Assert.Equal(SignInEnd.Refused, await work.WaitAsync(Limit));
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(1, terminals.CloseCount);
    }

    [Fact]
    public async Task Null_method_chooses_the_fresh_nondevice_method_and_never_uses_a_cached_choice()
    {
        var launcher = new Launcher();
        await using var session = await Runner(launcher).StartAsync(Descriptor(), null, "operation", default);
        var work = session.Completion;
        var link = await launcher.Next();
        await Initialize(link, """[{"methodId":"device","name":"Device","type":"agent"},{"methodId":"fresh","name":"Fresh","type":"agent"}]""");
        var login = await link.Agent.ReadAsync();
        Assert.Equal("fresh", login.GetProperty("params").GetProperty("methodId").GetString());
        await link.Agent.ReplyAsync(login, "{}");
        Assert.Equal(SignInEnd.SignedIn, await work.WaitAsync(Limit));
    }

    [Theory]
    [InlineData(-32800, SignInEnd.Cancelled)]
    [InlineData(-32603, SignInEnd.Refused)]
    public async Task Login_failure_returns_only_a_fixed_end_and_never_logs_raw_provider_material(int code, SignInEnd expected)
    {
        var launcher = new Launcher();
        using var log = new StringWriter();
        var runner = new SignInRunner(new(), new Terminals(), launcher, "/probe", TimeProvider.System, log);
        await using var session = await runner.StartAsync(Descriptor(), "actual", "operation", default);
        var work = session.Completion;
        var link = await launcher.Next();
        await Initialize(link, AgentMethod);
        var login = await link.Agent.ReadAsync();
        await link.Agent.ReplyErrorAsync(login, code, "RAW_LOGIN_PRIVATE_CANARY");
        Assert.Equal(expected, await work.WaitAsync(Limit));
        Assert.DoesNotContain("RAW_LOGIN_PRIVATE_CANARY", log.ToString());
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task Terminal_capability_false_refuses_even_an_advertised_terminal_method()
    {
        var launcher = new Launcher();
        var terminals = new Terminals();
        await using var session = await Runner(launcher, terminals).StartAsync(Descriptor(), "cli", "operation", default);
        var work = session.Completion;
        var link = await launcher.Next();
        await Initialize(link, TerminalMethod);
        Assert.Equal(SignInEnd.Refused, await work.WaitAsync(Limit));
        Assert.False(terminals.Started.Reader.TryRead(out _));
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task Terminal_zero_with_fresh_auth_required_is_refused()
    {
        var launcher = new Launcher { CanRunTerminalAuthentication = true };
        var terminals = new Terminals();
        await using var session = await Runner(launcher, terminals).StartAsync(Descriptor(), "cli", "operation", default);
        var work = session.Completion;
        await Initialize(await launcher.Next(), TerminalMethod);
        await terminals.Started.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        terminals.End(0);
        var verification = await launcher.Next();
        await Initialize(verification, TerminalMethod);
        var probe = await verification.Agent.ReadAsync();
        await verification.Agent.ReplyErrorAsync(probe, -32000, "AUTH_PRIVATE_CANARY");
        Assert.Equal(SignInEnd.Refused, await work.WaitAsync(Limit));
        Assert.True(verification.Disposed);
        Assert.Equal(1, terminals.CloseCount);
    }

    [Fact]
    public async Task Terminal_url_chunks_produce_once_only_code_input_and_a_new_generation_then_cancel_joins_it()
    {
        var board = new ChallengeBoard();
        var changes = Channel.CreateUnbounded<string>();
        board.Changed += id => changes.Writer.TryWrite(id);
        var launcher = new Launcher { CanRunTerminalAuthentication = true };
        var terminals = new Terminals();
        await using var session = await Runner(launcher, terminals, board).StartAsync(Descriptor(), "cli", "operation", default);
        var work = session.Completion;
        await Initialize(await launcher.Next(), TerminalMethod);
        await terminals.Started.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        terminals.Emit("a prompt htt");
        terminals.Emit("ps://example.invalid/?URL_PRIVATE_CANARY=one%2Btwo\n");
        await changes.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        var challenge = Assert.IsType<Challenge>(board.Current("operation"));
        Assert.True(challenge.WantsCode);
        Assert.Equal("terminal", challenge.TerminalId);
        await board.SubmitAsync("operation", challenge.Generation, "CODE_PRIVATE_CANARY", default);
        Assert.Equal("CODE_PRIVATE_CANARY\r"u8.ToArray(), Assert.Single(terminals.Written));
        var next = Assert.IsType<Challenge>(board.Current("operation"));
        Assert.True(next.Generation > challenge.Generation);
        Assert.Equal(challenge.Link!.OriginalString, next.Link!.OriginalString);
        await Assert.ThrowsAsync<Assembly.Application.Common.Calls.CallFailed>(() => board.SubmitAsync("operation", challenge.Generation, "again", default));
        await session.CancelAsync().WaitAsync(Limit);
        Assert.Equal(SignInEnd.Cancelled, await work);
        Assert.Null(board.Current("operation"));
        Assert.Equal(1, terminals.CloseCount);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_joins_pending_code_or_reports_a_fixed_code_delivery_failure_without_raw_material(bool failWrite)
    {
        var board = new ChallengeBoard();
        var changes = Channel.CreateUnbounded<string>();
        board.Changed += id => changes.Writer.TryWrite(id);
        var launcher = new Launcher { CanRunTerminalAuthentication = true };
        var terminals = new Terminals { HoldWrites = !failWrite, FailWrites = failWrite };
        using var log = new StringWriter();
        var runner = new SignInRunner(board, terminals, launcher, "/probe", TimeProvider.System, log);
        await using var session = await runner.StartAsync(Descriptor(), "cli", "operation", default);
        var work = session.Completion;
        await Initialize(await launcher.Next(), TerminalMethod);
        await terminals.Started.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        terminals.Emit("https://example.invalid/?safe=one\n");
        await changes.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        var challenge = Assert.IsType<Challenge>(board.Current("operation"));
        var delivery = board.SubmitAsync("operation", challenge.Generation, "CODE_PRIVATE_CANARY", default);
        await terminals.WriteEntered.Task.WaitAsync(Limit);
        if (failWrite)
        {
            var error = await Assert.ThrowsAsync<Assembly.Application.Common.Calls.CallFailed>(() => delivery);
            Assert.Equal("signin_write_failed", error.Reason);
            Assert.DoesNotContain("PRIVATE_CANARY", error.ToString());
        }
        await session.CancelAsync().WaitAsync(Limit);
        if (!failWrite) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.Equal(SignInEnd.Cancelled, await work);
        Assert.Equal(1, terminals.CloseCount);
        Assert.Null(board.Current("operation"));
        Assert.DoesNotContain("PRIVATE_CANARY", log.ToString());
    }

    private static SignInRunner Runner(Launcher launcher, Terminals? terminals = null, ChallengeBoard? board = null) =>
        new(board ?? new(), terminals ?? new(), launcher, "/probe", TimeProvider.System, TextWriter.Null);
    private static string Method(JsonElement request) => request.GetProperty("method").GetString()!;
    private static async Task Initialize(Link link, string methods)
    {
        var request = await link.Agent.ReadAsync();
        Assert.Equal("initialize", Method(request));
        await link.Agent.ReplyAsync(request, "{\"protocolVersion\":2,\"capabilities\":{},\"authMethods\":" + methods + "}");
    }

    private sealed class Launcher : IAgentLauncher
    {
        private readonly Channel<Link> _launched = Channel.CreateUnbounded<Link>();
        public bool CanRunTerminalAuthentication { get; init; }
        public TaskCompletionSource? ReleaseLaunch { get; init; }
        public int LaunchCount { get; private set; }
        public List<(AgentDescriptor Descriptor, IReadOnlyDictionary<string, string> Environment)> Captured { get; } = [];
        public IReadOnlyList<AgentDescriptor> Agents => [Descriptor()];
        public Task<Link> Next() => _launched.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public async Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
            IPermissionPolicy policy, StageTimes stages, CancellationToken ct)
        {
            LaunchCount++;
            Captured.Add((descriptor, environment));
            var link = new Link(policy);
            _launched.Writer.TryWrite(link);
            if (ReleaseLaunch is not null) await ReleaseLaunch.Task;
            return link;
        }
        public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
            StageTimes stages, CancellationToken cancellationToken) => throw new InvalidOperationException("ordinary configured launcher must never run");
        public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("shared launcher must never run");
        public void ValidateFolder(string folder) { }
    }

    private sealed class Link : IAgentLink
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeAgent Agent { get; } = new();
        public AcpClient Client { get; }
        public CountWrites Writes { get; }
        public Task Exited => _exit.Task;
        public bool Disposed { get; private set; }
        public Link(IPermissionPolicy policy)
        {
            Writes = new CountWrites(Agent.ClientWrites);
            Client = new(Agent.ClientReads, Writes, policy, TranscriptLog.Discard());
        }
        public async ValueTask DisposeAsync()
        {
            if (Disposed) return;
            Disposed = true;
            Agent.CloseOutput();
            await Client.DisposeAsync();
            _exit.TrySetResult();
        }
    }

    private sealed class CountWrites(Stream inner) : Stream
    {
        public int Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { Count++; inner.Write(buffer, offset, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Count++;
            return inner.WriteAsync(buffer, ct);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class Terminals : IProgramTerminals
    {
        private readonly Channel<ReadOnlyMemory<byte>> _output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<ProgramStart> Started { get; } = Channel.CreateUnbounded<ProgramStart>();
        public List<byte[]> Written { get; } = [];
        public bool HoldWrites { get; init; }
        public bool FailWrites { get; init; }
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CloseCount { get; private set; }
        public Task<ProgramTerminal> StartProgramAsync(ProgramStart start, CancellationToken ct)
        {
            Started.Writer.TryWrite(start);
            return Task.FromResult(new ProgramTerminal("terminal", 42));
        }
        public void End(int code) { _output.Writer.TryComplete(); _exit.TrySetResult(code); }
        public void Emit(string text) => _output.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(text));
        public IAsyncEnumerable<ReadOnlyMemory<byte>> OutputAsync(string terminal, CancellationToken ct) => _output.Reader.ReadAllAsync(ct);
        public Task<int> ExitedAsync(string terminal, CancellationToken ct) => _exit.Task.WaitAsync(ct);
        public async ValueTask WriteAsync(string terminal, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            WriteEntered.TrySetResult();
            if (FailWrites) throw new IOException("WRITE_PRIVATE_CANARY");
            if (HoldWrites) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            Written.Add(data.ToArray());
        }
        public ValueTask CloseAsync(string terminal, CancellationToken ct) { CloseCount++; End(130); return ValueTask.CompletedTask; }
    }
}
