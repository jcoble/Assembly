using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Terminals;
using Assembly.Application.Features.Agents;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Hosting;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Auth;
using Assembly.Tests.Core.Acp;
using Assembly.Tests.Engine;
using Assembly.Tests.Features.Chat.Sessions;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class SignInEffectsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(8);
    [Fact]
    public async Task Registered_real_worker_confirms_started_before_login_without_fabricating_auth()
    {
        await using var f = new Fixture(worker: true);
        f.Start();
        var link = await f.Launcher.Next();
        Assert.Equal("initialize", (await link.Agent.ReadAsync()).GetProperty("method").GetString());
        await f.StateAsync("engine_outbox", "begin_sign_in/operation", "done");
        Assert.Equal("starting", f.State("operation"));
        Assert.Equal("unverified", f.Core.Text("SELECT auth_state FROM engine_provider WHERE id='codex/'"));
        Assert.Equal(0, f.Core.Count("engine_session"));
        Assert.Equal(0, f.Core.Count("engine_message"));
        Assert.Equal(0, f.Core.Count("engine_needs_me"));
        Assert.Equal(0, f.Core.Count("engine_run"));
        Assert.Equal(0, f.Launcher.OrdinaryLaunches);
    }
    [Fact]
    public async Task Actual_url_consent_is_required_then_login_updates_the_exact_provider()
    {
        await using var f = new Fixture(worker: true);
        f.Start();
        var link = await f.Launcher.Next();
        var login = await Initialize(link);
        const string original = "https://example.invalid/%2f?fixture=exact%2Bvalue";
        await link.Agent.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":991,\"method\":\"elicitation/create\",\"params\":{\"mode\":\"url\",\"url\":"
            + JsonSerializer.Serialize(original) + ",\"elicitationId\":\"url\",\"requestId\":" + login.GetProperty("id").GetRawText() + "}}");
        await f.StateAsync("engine_sign_in", "operation", "waiting_for_person");
        var challenge = Assert.IsType<Challenge>(f.Board.Current("operation"));
        Assert.Equal(original, challenge.Link!.OriginalString);
        Assert.Equal("unverified", f.Core.Text("SELECT auth_state FROM engine_provider WHERE id='codex/'"));
        await f.Board.AcknowledgeUrlAsync("operation", challenge.Generation, original, default);
        Assert.Equal("accept", (await link.Agent.ReadAsync()).GetProperty("result").GetProperty("action").GetString());
        await link.Agent.ReplyAsync(login, "{}");
        await f.StateAsync("engine_sign_in", "operation", "signed_in");
        Assert.Equal("signed_in", f.Core.Text("SELECT auth_state FROM engine_provider WHERE id='codex/'"));
        Assert.Null(f.Board.Current("operation"));
        Assert.Equal(1, f.Launcher.SignIns);
        Assert.DoesNotContain(f.Core.Events(), e => e.Data.ToString()!.Contains(original, StringComparison.Ordinal));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind NOT IN ('probe_provider','begin_sign_in','cancel_sign_in')"));
    }
    [Fact]
    public async Task Each_real_challenge_generation_invalidates_waiting_but_clear_does_not()
    {
        await using var f = new Fixture();
        var effect = f.Begin();
        Assert.Equal(EffectDisposition.Confirmed, (await f.Effects.ExecuteAsync(effect, default)).Disposition);
        var link = await f.Launcher.Next();
        await Initialize(link);
        f.Board.Post("operation", new("https://example.invalid/one"), null, null, false, null, null);
        await f.StateAsync("engine_sign_in", "operation", "waiting_for_person");
        var version = f.Version("operation");
        f.Board.Post("operation", new("https://example.invalid/two"), null, null, false, null, null);
        await f.VersionAsync("operation", version + 1);
        f.Board.Clear("operation");
        await f.Effects.DisposeAsync();
        Assert.Equal(version + 1, f.Version("operation"));
        Assert.True(link.Disposed);
    }
    [Theory]
    [InlineData("claim")]
    [InlineData("payload")]
    [InlineData("order")]
    [InlineData("session")]
    public async Task Admission_rejects_incorrect_original_claim_or_target_before_capture(string mutation)
    {
        await using var f = new Fixture();
        var effect = f.Begin();
        effect = mutation switch
        {
            "claim" => effect with { Attempt = effect.Attempt + 1 },
            "payload" => effect with { Payload = JsonSerializer.SerializeToElement(new BeginSignInEffect("operation", "other", null), EngineJson.Default.BeginSignInEffect) },
            "order" => effect with { OrderKey = "provider/other/" },
            _ => effect with { SessionId = "invented" }
        };
        var result = await f.Effects.ExecuteAsync(effect, default);
        Assert.Equal(EffectDisposition.Failed, result.Disposition);
        Assert.Equal(0, f.Port.Captures);
        Assert.Equal(0, f.Launcher.SignIns);
    }
    [Fact]
    public async Task Cancel_before_begin_registration_starts_no_login()
    {
        await using var f = new Fixture();
        var begin = f.Begin();
        f.Cancel();
        Assert.Equal(EffectDisposition.Failed, (await f.Effects.ExecuteAsync(begin, default)).Disposition);
        Assert.Equal(0, f.Launcher.SignIns);
        Assert.Equal(0, f.Port.Captures);
    }
    [Fact]
    public async Task Cancel_while_capture_waits_prevents_lazy_completion_from_starting()
    {
        await using var f = new Fixture();
        f.Port.HoldCapture = true;
        var begin = f.Begin();
        var work = f.Effects.ExecuteAsync(begin, default);
        await f.Port.Entered.Task.WaitAsync(Limit);
        f.Cancel();
        f.Port.Release.TrySetResult();
        var receipt = await work.WaitAsync(Limit);
        Assert.Equal(EffectDisposition.Failed, receipt.Disposition);
        f.Complete(begin, receipt);
        var cancel = f.Claim("cancel_sign_in/operation");
        Assert.Equal(EffectDisposition.Confirmed, (await f.Effects.ExecuteAsync(cancel, default)).Disposition);
        Assert.Equal(0, f.Launcher.SignIns);
        Assert.Equal("cancelled", f.State("operation"));
    }
    [Fact]
    public async Task Cancel_after_registration_joins_the_exact_owned_link_and_clears_challenge()
    {
        await using var f = new Fixture();
        var begin = f.Begin();
        f.Complete(begin, await f.Effects.ExecuteAsync(begin, default));
        var link = await f.Launcher.Next();
        await Initialize(link);
        f.Board.Post("operation", new("https://example.invalid/cancel"), null, null, false, null, null);
        f.Cancel();
        var cancel = f.Claim("cancel_sign_in/operation");
        var result = await f.Effects.ExecuteAsync(cancel, default).WaitAsync(Limit);
        Assert.Equal(new SignInEffectData("operation", "stopped"), result.Data.Deserialize(EngineJson.Default.SignInEffectData));
        Assert.True(link.Disposed);
        Assert.Null(f.Board.Current("operation"));
        Assert.Equal("cancelled", f.State("operation"));
        Assert.Equal(0, f.Port.Retires);
        Assert.Equal(1, f.Launcher.SignIns);
    }
    [Fact]
    public async Task Worker_owned_expiry_stops_the_registered_login_without_an_effect_timer()
    {
        await using var f = new Fixture(worker: true);
        f.Start();
        var link = await f.Launcher.Next();
        await Initialize(link);
        await f.StateAsync("engine_outbox", "begin_sign_in/operation", "done");
        f.Core.Time.Advance(TimeSpan.FromSeconds(300));
        await f.StateAsync("engine_sign_in", "operation", "expired");
        await f.StateAsync("engine_outbox", "cancel_sign_in/operation", "done");
        Assert.True(link.Disposed);
        Assert.Equal(0, f.Port.Retires);
    }
    [Fact]
    public async Task Runtime_shutdown_joins_supervisor_without_self_await_or_extra_login()
    {
        await using var f = new Fixture(worker: true);
        f.Start();
        var link = await f.Launcher.Next();
        await Initialize(link);
        await f.Runtime.DisposeAsync().AsTask().WaitAsync(Limit);
        await f.Effects.DisposeAsync();
        Assert.True(link.Disposed);
        Assert.Null(f.Board.Current("operation"));
        Assert.Equal(1, f.Launcher.SignIns);
        Assert.Equal(0, f.Port.Retires);
    }
    [Fact]
    public async Task Reconcile_without_a_registered_handle_never_restarts_or_claims_success()
    {
        await using var f = new Fixture();
        var effect = f.Begin();
        var result = await f.Effects.ReconcileAsync(effect, default);
        Assert.Equal(EffectDisposition.Failed, result.Disposition);
        Assert.Equal("interrupted", result.Error);
        Assert.Equal(0, f.Launcher.SignIns);
        Assert.Equal(0, f.Port.Captures);
    }
    [Fact]
    public async Task A_recovered_new_claim_stops_the_original_handle_without_adopting_its_result()
    {
        await using var f = new Fixture();
        var begin = f.Begin();
        await f.Effects.ExecuteAsync(begin, default);
        var link = await f.Launcher.Next();
        await Initialize(link);
        f.Apply(new RecoverEffect(), begin.Id, f.OutboxVersion(begin.Id), worker: true);
        var recovered = f.Claim(begin.Id);
        Assert.Equal(begin.Attempt + 1, recovered.Attempt);
        var result = await f.Effects.ReconcileAsync(recovered, default).WaitAsync(Limit);
        Assert.Equal(EffectDisposition.Failed, result.Disposition);
        Assert.True(link.Disposed);
        Assert.Equal(1, f.Launcher.SignIns);
        Assert.Equal(0, f.Port.Retires);
        Assert.NotEqual("signed_in", f.State("operation"));
    }
    [Fact]
    public async Task Cancelling_one_operation_preserves_another_configured_provider_attempt()
    {
        await using var f = new Fixture(worker: true);
        f.Start();
        var first = await f.Launcher.Next();
        await Initialize(first);
        f.Start("other-operation", "claude");
        var second = await f.Launcher.Next();
        var secondLogin = await Initialize(second);
        f.Cancel();
        await f.StateAsync("engine_outbox", "cancel_sign_in/operation", "done");
        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        await second.Agent.ReplyAsync(secondLogin, "{}");
        await f.StateAsync("engine_sign_in", "other-operation", "signed_in");
        Assert.Equal("cancelled", f.State("operation"));
        Assert.Equal(2, f.Launcher.SignIns);
    }
    [Fact]
    public async Task Replaced_configuration_while_capture_waits_cannot_start_a_replacement_login()
    {
        await using var f = new Fixture();
        f.Port.HoldCapture = true;
        var work = f.Effects.ExecuteAsync(f.Begin(), default);
        await f.Port.Entered.Task.WaitAsync(Limit);
        f.Launcher.Configured[0] = f.Launcher.Configured[0] with { Command = "changed-command" };
        f.Port.Release.TrySetResult();
        Assert.Equal(EffectDisposition.Failed, (await work.WaitAsync(Limit)).Disposition);
        await f.Effects.DisposeAsync();
        Assert.Equal(0, f.Launcher.SignIns);
        Assert.Equal(0, f.Port.Retires);
    }
    [Fact]
    public async Task Unsupported_method_has_fixed_failure_and_disposes_the_owned_link()
    {
        await using var f = new Fixture(worker: true);
        f.Start(method: "absent-method");
        var link = await f.Launcher.Next();
        var init = await link.Agent.ReadAsync();
        await link.Agent.ReplyAsync(init, """{"protocolVersion":2,"authMethods":[]}""");
        await f.StateAsync("engine_sign_in", "operation", "failed");
        Assert.Equal("unsupported", f.Core.Text("SELECT reason FROM engine_sign_in WHERE id='operation'"));
        await f.Effects.DisposeAsync();
        Assert.True(link.Disposed);
        Assert.Equal(0, f.Port.Retires);
    }
    [Fact]
    public async Task Caller_disconnect_after_admitted_begin_does_not_cancel_the_durable_attempt()
    {
        await using var f = new Fixture();
        using var caller = new CancellationTokenSource();
        var begin = f.Begin();
        f.Complete(begin, await f.Effects.ExecuteAsync(begin, caller.Token));
        caller.Cancel();
        var link = await f.Launcher.Next();
        var login = await Initialize(link);
        await link.Agent.ReplyAsync(login, "{}");
        await f.StateAsync("engine_sign_in", "operation", "signed_in");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_idle_shared_instance_retires_only_after_accepted_uncancelled_login(bool cancel)
    {
        await using var f = new Fixture();
        var old = await f.WarmShared();
        var begin = f.Begin();
        f.Complete(begin, await f.Effects.ExecuteAsync(begin, default));
        var dedicated = await f.Launcher.Next();
        var login = await Initialize(dedicated);
        if (cancel) f.Cancel();
        await dedicated.Agent.ReplyAsync(login, "{}"); // Deliberately late success after the durable cancel.
        if (cancel)
        {
            await f.Effects.ExecuteAsync(f.Claim("cancel_sign_in/operation"), default);
            Assert.Equal("cancelled", f.State("operation"));
            Assert.False(old.Disposed);
            Assert.Equal(0, f.Port.Retires);
        }
        else
        {
            await f.StateAsync("engine_sign_in", "operation", "signed_in");
            Assert.True(old.Disposed);
            Assert.True(old.Exited.IsCompleted);
            Assert.Equal(1, f.Port.Retires);
        }
    }
    [Fact]
    public async Task Exact_old_capture_never_retires_a_new_shared_replacement()
    {
        await using var f = new Fixture();
        var old = await f.WarmShared();
        var begin = f.Begin();
        f.Complete(begin, await f.Effects.ExecuteAsync(begin, default));
        var dedicated = await f.Launcher.Next();
        var login = await Initialize(dedicated);
        var target = ConfigTargetIdentity.Capture(f.Launcher, "codex");
        var oldCapture = Assert.IsAssignableFrom<ProviderAdapterCapture>(await f.Providers.CaptureAsync("codex", target.TargetId, default));
        await f.Providers.RetireAsync(oldCapture, default);
        var replacement = await f.WarmShared();
        await dedicated.Agent.ReplyAsync(login, "{}");
        await f.StateAsync("engine_sign_in", "operation", "signed_in");
        Assert.True(old.Disposed);
        Assert.False(replacement.Disposed);
        Assert.Equal(1, f.Port.Retires);
        Assert.Equal(2, f.Launcher.OrdinaryLaunches);
    }
    [Fact]
    public async Task Accepted_login_retires_the_capture_while_its_original_real_prompt_finishes_unchanged()
    {
        await using var f = new Fixture();
        var session = f.Sessions.Create("codex", "https://example.invalid/fixture");
        var host = f.Hosts.For(session.SessionId);
        var run = host.Send("original prompt");
        var shared = await f.Launcher.NextShared();
        await shared.StartAsync(configOptions: """[{"id":"model","category":"model","currentValue":"original-model"}]""");
        var prompt = await shared.ReadAsync("session/prompt");
        var begin = f.Begin();
        f.Complete(begin, await f.Effects.ExecuteAsync(begin, default));
        var dedicated = await f.Launcher.Next();
        var login = await Initialize(dedicated);
        await dedicated.Agent.ReplyAsync(login, "{}");
        await f.StateAsync("engine_sign_in", "operation", "signed_in");
        Assert.False(shared.Disposed);
        Assert.Equal(run, host.ActiveRunId);
        Assert.Equal(FakeLink.AcpSessionId, f.Sessions.Get(session.SessionId)!.AcpSessionId);
        Assert.Equal(1, f.Launcher.OrdinaryLaunches);
        await shared.FinishAsync(prompt, "{\"stopReason\":\"end_turn\"}");
        Assert.Equal(FakeLink.AcpSessionId, await shared.CloseAsync());
        await shared.Exited.WaitAsync(Limit);
        Assert.True(shared.Disposed);
    }
    [Theory]
    [InlineData(false, "adapter_error")]
    [InlineData(true, "adapter_crashed")]
    public async Task Actual_link_loss_is_not_invented_network_evidence(bool processExited, string reason)
    {
        await using var f = new Fixture(worker: true);
        f.Start();
        var link = await f.Launcher.Next();
        await Initialize(link);
        link.LoseOutput(processExited);
        await f.StateAsync("engine_sign_in", "operation", "failed");
        Assert.Equal(reason, f.Core.Text("SELECT reason FROM engine_sign_in WHERE id='operation'"));
        await f.Effects.DisposeAsync();
        Assert.True(link.Disposed);
        Assert.Equal(0, f.Port.Retires);
    }
    [Fact]
    public async Task Cancel_during_dedicated_verification_session_new_joins_the_link_and_preserves_another_attempt()
    {
        await using var f = new Fixture(worker: true, terminal: true);
        f.Start();
        var discovery = await f.Launcher.Next();
        var initialize = await discovery.Agent.ReadAsync();
        await discovery.Agent.ReplyAsync(initialize, """{"protocolVersion":2,"authMethods":[{"methodId":"terminal","name":"Terminal","type":"terminal"}]}""");
        await f.Terminals.Started.Task.WaitAsync(Limit);
        f.Terminals.End(0);
        var verification = await f.Launcher.Next();
        var verifyInitialize = await verification.Agent.ReadAsync();
        await verification.Agent.ReplyAsync(verifyInitialize, """{"protocolVersion":2}""");
        var opening = await verification.Agent.ReadAsync();
        Assert.Equal("session/new", opening.GetProperty("method").GetString());
        f.Start("other-operation", "claude");
        var other = await f.Launcher.Next();
        var otherLogin = await Initialize(other);
        f.Cancel();
        await f.StateAsync("engine_outbox", "cancel_sign_in/operation", "done");
        Assert.True(discovery.Disposed);
        Assert.True(verification.Disposed);
        Assert.True(verification.Exited.IsCompleted);
        Assert.False(other.Disposed);
        Assert.Equal(0, f.Core.Count("engine_session"));
        Assert.Equal(0, f.Core.Count("engine_message"));
        Assert.Equal("cancelled", f.State("operation"));
        await other.Agent.ReplyAsync(otherLogin, "{}");
        await f.StateAsync("engine_sign_in", "other-operation", "signed_in");
        Assert.Equal(3, f.Launcher.SignIns);
        Assert.Equal(0, f.Port.Retires);
    }

    private static async Task<JsonElement> Initialize(Link link)
    {
        var init = await link.Agent.ReadAsync();
        Assert.Equal("initialize", init.GetProperty("method").GetString());
        await link.Agent.ReplyAsync(init, """{"protocolVersion":2,"authMethods":[{"methodId":"actual","name":"Actual","type":"agent"}]}""");
        var login = await link.Agent.ReadAsync();
        Assert.Equal("auth/login", login.GetProperty("method").GetString());
        return login;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Topics Topics { get; } = new();
        public EngineFixture Core { get; }
        public Launcher Launcher { get; } = new();
        public Port Port { get; } = new();
        public ChallengeBoard Board { get; } = new();
        public NoTerminals Terminals { get; } = new();
        public EngineRuntime Runtime { get; }
        public SignInEffects Effects { get; private set; } = null!;
        private readonly SessionRegistry _hosts;
        public SessionRegistry Hosts => _hosts;
        public SessionStore Sessions { get; }
        public IProviderAdapters Providers => _hosts;
        private readonly LiveHub _hub;
        private readonly BackgroundWork _work;
        private readonly IDisposable _events;
        private readonly Channel<EngineEvent> _changes = Channel.CreateUnbounded<EngineEvent>();
        public Fixture(bool worker = false, bool terminal = false)
        {
            Core = new(topics: Topics);
            Launcher.CanRunTerminalAuthentication = terminal;
            var rows = RowStore.Open(Core.Db);
            var sessions = Sessions = SessionStore.Open(Core.Db, Core.Time, "/unused-checkouts");
            _hub = new(rows);
            _work = new(Core.Time, TextWriter.Null);
            _hosts = new(_hub, sessions, Launcher, Core.Time, TextWriter.Null, _work);
            Port.Inner = _hosts;
            var runner = new SignInRunner(Board, Terminals, Launcher, "/unused-probe", Core.Time, TextWriter.Null);
            Runtime = EngineRuntime.Start(Core.Db, Topics, Core.Time, Core.Limits,
                effectsReady: worker ? Task.CompletedTask : new TaskCompletionSource().Task,
                isConfiguredProvider: (provider, account) => account == "" && Launcher.Agents.Any(a => a.Id == provider),
                effectsFactory: runtime =>
                {
                    Effects = new(Core.Db, runner, Launcher, Port, Board, runtime, Core.Time);
                    return new EffectRouter(new AdapterEffects(Core.Db, _hosts, sessions, rows))
                        .Add("begin_sign_in", Effects).Add("cancel_sign_in", Effects);
                });
            _events = Runtime.Publisher.Register(e => _changes.Writer.TryWrite(e), out _);
        }
        public async Task<FakeLink> WarmShared()
        {
            var target = ConfigTargetIdentity.Capture(Launcher, "codex");
            var probe = Providers.ProbeAsync(target.Descriptor with { Env = target.Environment }, target.TargetId, "/unused-probe", default);
            var link = await Launcher.NextShared();
            await link.StartAsync("warm-probe");
            await link.CloseAsync();
            await probe.WaitAsync(Limit);
            return link;
        }
        public void Start(string operation = "operation", string provider = "codex", string? method = null) =>
            Assert.True(Apply(new StartSignIn(provider, "", method, null), operation, 0).Applied);
        public Effect Begin() { Start(); return Claim("begin_sign_in/operation"); }
        public void Cancel() => Assert.True(Apply(new CancelSignIn(), "operation", Version("operation")).Applied);
        public Effect Claim(string id)
        {
            Assert.True(Apply(new ClaimEffect(), id, OutboxVersion(id), worker: true).Applied);
            return EffectTransitions.Read(Core.Db, id, default)!;
        }
        public void Complete(Effect effect, EffectReceipt receipt) =>
            Assert.True(Apply(new CompleteEffect(receipt), effect.Id, OutboxVersion(effect.Id), worker: true).Applied);
        public CommandResult Apply(EngineCommand command, string id, long version, bool worker = false) =>
            ((IEngineRuntime)Runtime).Write(scope => Runtime.Commands.Apply(scope,
                new(Guid.NewGuid(), Runtime.Machine, new(worker ? ActorKind.System : ActorKind.Window, worker ? "engine.worker" : "owner"),
                    id, version, 0, Core.Time.GetUtcNow().ToUnixTimeMilliseconds()), command));
        public string? State(string id) => Core.State("engine_sign_in", id);
        public long Version(string id) => Core.Number("SELECT version FROM engine_sign_in WHERE id=$id", new SqlArg("$id", id));
        public long OutboxVersion(string id) => Core.Number("SELECT version FROM engine_outbox WHERE id=$id", new SqlArg("$id", id));
        public async Task StateAsync(string table, string id, string state)
        {
            using var deadline = new CancellationTokenSource(Limit);
            while (Core.State(table, id) != state) await _changes.Reader.ReadAsync(deadline.Token);
        }
        public async Task VersionAsync(string id, long version)
        {
            using var deadline = new CancellationTokenSource(Limit);
            while (Version(id) < version) await _changes.Reader.ReadAsync(deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Port.Release.TrySetResult();
            await Runtime.DisposeAsync();
            _events.Dispose();
            await _hosts.DisposeAsync();
            await _work.DisposeAsync();
            _hub.Dispose();
            Core.Dispose();
        }
    }

    private sealed class Port : IProviderAdapters
    {
        public IProviderAdapters Inner { get; set; } = null!;
        public bool HoldCapture { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Captures { get; private set; }
        public int Retires { get; private set; }
        public async Task<ProviderAdapterCapture?> CaptureAsync(string provider, string target, CancellationToken ct)
        {
            Captures++;
            Entered.TrySetResult();
            if (HoldCapture) await Release.Task;
            return await Inner.CaptureAsync(provider, target, ct);
        }
        public Task RetireAsync(ProviderAdapterCapture capture, CancellationToken ct) { Retires++; return Inner.RetireAsync(capture, ct); }
        public Task<ProviderProbeResult> ProbeAsync(AgentDescriptor descriptor, string configurationTargetId, string probeFolder, CancellationToken ct) =>
            throw new InvalidOperationException("Sign-in effects must not run a shared readiness probe");
    }

    private sealed class Launcher : IAgentLauncher
    {
        public List<AgentDescriptor> Configured { get; } = [new("codex", "Codex", "fixture-adapter", [], null), new("claude", "Claude", "fixture-adapter", [], null)];
        public IReadOnlyList<AgentDescriptor> Agents => Configured;
        public bool CanRunTerminalAuthentication { get; set; }
        public int SignIns { get; private set; }
        public int OrdinaryLaunches { get; private set; }
        private readonly Channel<Link> _links = Channel.CreateUnbounded<Link>();
        private readonly Channel<FakeLink> _shared = Channel.CreateUnbounded<FakeLink>();
        public Task<FakeLink> NextShared() => _shared.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public Task<Link> Next() => _links.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
            IPermissionPolicy policy, StageTimes stages, CancellationToken ct)
        {
            SignIns++;
            var link = new Link(policy);
            _links.Writer.TryWrite(link);
            return Task.FromResult<IAgentLink>(link);
        }
        public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken ct)
        { OrdinaryLaunches++; throw new InvalidOperationException("No ordinary provider work is allowed"); }
        public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
            StageTimes stages, CancellationToken ct)
        {
            OrdinaryLaunches++;
            var link = new FakeLink();
            _shared.Writer.TryWrite(link);
            return Task.FromResult<IAgentLink>(link);
        }
        public void ValidateFolder(string folder) { }
    }

    private sealed class Link : IAgentLink
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeAgent Agent { get; } = new();
        public AcpClient Client { get; }
        public Task Exited => _exit.Task;
        public bool Disposed { get; private set; }
        public Link(IPermissionPolicy policy) => Client = new(Agent.ClientReads, Agent.ClientWrites, policy, TranscriptLog.Discard());
        public void LoseOutput(bool exited) { if (exited) _exit.TrySetResult(); Agent.CloseOutput(); }
        public async ValueTask DisposeAsync()
        {
            if (Disposed) return;
            Disposed = true;
            Agent.CloseOutput();
            await Client.DisposeAsync();
            _exit.TrySetResult();
        }
    }

    private sealed class NoTerminals : IProgramTerminals
    {
        private readonly Channel<ReadOnlyMemory<byte>> _output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProgramStart> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProgramTerminal> StartProgramAsync(ProgramStart start, CancellationToken ct)
        { Started.TrySetResult(start); return Task.FromResult(new ProgramTerminal("owned-terminal", 42)); }
        public void End(int code) { _output.Writer.TryComplete(); _exit.TrySetResult(code); }
        public IAsyncEnumerable<ReadOnlyMemory<byte>> OutputAsync(string terminal, CancellationToken ct) => _output.Reader.ReadAllAsync(ct);
        public Task<int> ExitedAsync(string terminal, CancellationToken ct) => _exit.Task.WaitAsync(ct);
        public ValueTask WriteAsync(string terminal, ReadOnlyMemory<byte> data, CancellationToken ct) => throw new InvalidOperationException();
        public ValueTask CloseAsync(string terminal, CancellationToken ct) { End(130); return ValueTask.CompletedTask; }
    }
}
