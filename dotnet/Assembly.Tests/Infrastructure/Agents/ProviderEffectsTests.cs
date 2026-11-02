using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
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
using Assembly.Tests.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Infrastructure.Agents;

public sealed class ProviderEffectsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Registered_worker_coalesces_check_and_commits_exact_facts_confirmation_and_push()
    {
        await using var h = new Harness();
        Assert.True(h.Apply(new CheckProvider()).Applied);
        Assert.True(h.Apply(new CheckProvider(), 1).Applied);
        Assert.Equal(1, h.Core.Count("engine_outbox"));
        h.Ready.TrySetResult();
        await h.Until(() => h.Text("SELECT state FROM engine_outbox") == "done");
        Assert.Equal(1, h.Probe.Calls);
        var pushed = await h.ProviderChange(2);
        Assert.Equal(h.Runtime.Machine, pushed.Machine);
        Assert.Equal(h.Target, pushed.EntityId);
        Assert.Equal("unverified", pushed.Data.State);
        Assert.Equal("unverified", h.Text("SELECT auth_state FROM engine_provider"));
        Assert.Equal("installed", h.Text("SELECT installation FROM engine_provider"));
        Assert.Equal(2, h.Core.Number("SELECT COUNT(*) FROM engine_event WHERE kind='provider.changed'"));
        var data = JsonDocument.Parse(h.Text("SELECT receipt_json FROM engine_outbox")!).RootElement.GetProperty("data");
        Assert.Equal(new ProviderEffectData(h.Target, 1), data.Deserialize(EngineJson.Default.ProviderEffectData));
        var facts = JsonSerializer.Deserialize(h.Text("SELECT facts_json FROM engine_provider")!, ProvidersJson.Default.ProviderFacts)!;
        Assert.Equal("Fixture", facts.Name);
        Assert.Equal("1.2", facts.Version);
        Assert.Equal(1000, facts.CheckedMs);
        Assert.Equal(h.Account, h.Probe.Descriptor!.Env!["CODEX_HOME"]);
        Assert.Equal(ConfigTargetIdentity.Of(h.Probe.Descriptor, h.Probe.Descriptor.Env!), h.Probe.Target);
        Assert.Equal(0, h.Launcher.Launches);
    }

    [Theory]
    [InlineData("not_installed", "not_installed", "could_not_check")]
    [InlineData("untrusted private detail", "installed", "could_not_check")]
    [InlineData(null, "installed", "needs_sign_in")]
    public async Task Actual_catalogue_evidence_maps_separate_installation_auth_and_safe_reason(string? problem, string installation, string auth)
    {
        await using var h = new Harness();
        h.Probe.Result = new(null, null, null, problem is null, problem);
        var effect = h.Claim();
        var receipt = await h.Effects.ExecuteAsync(effect, default);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        Assert.Equal(installation, h.Text("SELECT installation FROM engine_provider"));
        Assert.Equal(auth, h.Text("SELECT auth_state FROM engine_provider"));
        Assert.DoesNotContain("private detail", h.Text("SELECT facts_json FROM engine_provider")!);
        Assert.Contains(h.Text("SELECT reason FROM engine_provider")!, new[] { "not_installed", "adapter_error", "version_unknown" });
    }

    [Theory]
    [InlineData("readiness", "signed_in")]
    [InlineData("unknown", "signed_in")]
    [InlineData("exception", "signed_in")]
    [InlineData("negative", "needs_sign_in")]
    public async Task Existing_authoritative_login_survives_nonnegative_probe_but_real_negative_wins(string evidence, string expected)
    {
        await using var h = new Harness();
        h.Login();
        Assert.Null(h.Text("SELECT facts_json FROM engine_provider"));
        if (evidence == "exception") h.Probe.Error = new IOException("secret exception canary");
        else if (evidence == "unknown") h.Probe.Result = new(null, null, null, false, null);
        else if (evidence == "negative") h.Probe.Result = new(null, null, null, true, null);
        var result = await h.Effects.ExecuteAsync(h.ClaimExisting(), default);
        Assert.Equal(EffectDisposition.Confirmed, result.Disposition);
        Assert.Equal(expected, h.Text("SELECT auth_state FROM engine_provider"));
        if (evidence == "exception")
        {
            Assert.Null(h.Text("SELECT facts_json FROM engine_provider"));
            Assert.Equal("adapter_error", h.Text("SELECT reason FROM engine_provider"));
        }
        else
        {
            using var facts = JsonDocument.Parse(h.Text("SELECT facts_json FROM engine_provider")!);
            Assert.Equal(expected == "signed_in" ? "signedIn" : "needsSignIn", facts.RootElement.GetProperty("auth").GetString());
        }
        Assert.DoesNotContain("secret exception canary", h.Text("SELECT group_concat(data_json) FROM engine_event")!);
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("kind")]
    [InlineData("session")]
    [InlineData("run")]
    [InlineData("order")]
    [InlineData("attempt")]
    [InlineData("version")]
    [InlineData("payload")]
    public async Task Actual_persisted_owner_and_original_claim_are_checked_before_any_probe(string fault)
    {
        await using var h = new Harness();
        var effect = h.Claim();
        h.Core.SeedSession("other");
        h.Core.SeedRun("other", "other");
        var sql = fault switch
        {
            "machine" => "UPDATE engine_outbox SET machine='other'",
            "kind" => "UPDATE engine_outbox SET kind='begin_sign_in'",
            "session" => "UPDATE engine_outbox SET session_id='other'",
            "run" => "UPDATE engine_outbox SET run_id='other'",
            "order" => "UPDATE engine_outbox SET order_key='provider/other'",
            "attempt" => "UPDATE engine_outbox SET attempt=attempt+1",
            "version" => "UPDATE engine_provider SET version=version+1",
            _ => "UPDATE engine_outbox SET payload_json=json_set(payload_json,'$.providerVersion',99)"
        };
        h.Core.Db.Writer.Write("test.actual-mismatch", scope => scope.Execute(sql));
        Assert.Equal(EffectDisposition.Unknown, (await h.Effects.ExecuteAsync(effect, default)).Disposition);
        Assert.Equal(0, h.Probe.Calls);
        Assert.Null(h.Text("SELECT facts_json FROM engine_provider"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replaced_configuration_never_launches_or_records_for_the_old_context(bool whileAwaiting)
    {
        await using var h = new Harness();
        var effect = h.Claim();
        h.Probe.Block = whileAwaiting;
        Task<EffectReceipt>? pending = null;
        if (whileAwaiting)
        {
            pending = h.Effects.ExecuteAsync(effect, default);
            await h.Probe.Entered.Task.WaitAsync(Limit);
        }
        h.Launcher.Agent = h.Launcher.Agent with { Command = "replacement", Env = new Dictionary<string, string> { ["CODEX_HOME"] = "/other/context" } };
        h.Probe.Release.TrySetResult();
        var result = await (pending ?? h.Effects.ExecuteAsync(effect, default));
        Assert.Equal(EffectDisposition.Unknown, result.Disposition);
        Assert.Equal(whileAwaiting ? 1 : 0, h.Probe.Calls);
        Assert.Null(h.Text("SELECT facts_json FROM engine_provider"));
    }

    [Fact]
    public async Task Captured_environment_and_descriptor_are_unchanged_when_launcher_collections_mutate()
    {
        await using var h = new Harness();
        h.Probe.Block = true;
        var pending = h.Effects.ExecuteAsync(h.Claim(), default);
        await h.Probe.Entered.Task.WaitAsync(Limit);
        var original = h.Probe.Descriptor!;
        ((Dictionary<string, string>)h.Launcher.Agent.Env!)["CODEX_HOME"] = "/replacement";
        Assert.Equal(h.Account, original.Env!["CODEX_HOME"]);
        h.Probe.Release.TrySetResult();
        Assert.Equal(EffectDisposition.Unknown, (await pending).Disposition);
        Assert.Null(h.Text("SELECT facts_json FROM engine_provider"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_or_runtime_stop_during_probe_prevents_any_late_callback(bool runtimeStop)
    {
        await using var h = new Harness();
        h.Probe.Block = true;
        using var caller = new CancellationTokenSource();
        var before = h.Core.Count("engine_command_receipt");
        var pending = h.Effects.ExecuteAsync(h.Claim(), caller.Token);
        await h.Probe.Entered.Task.WaitAsync(Limit);
        before = h.Core.Count("engine_command_receipt");
        if (runtimeStop) await h.Runtime.DisposeAsync();
        else caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(before, h.Core.Count("engine_command_receipt"));
        Assert.Null(h.Text("SELECT facts_json FROM engine_provider"));
    }

    [Fact]
    public async Task Reconciliation_is_unknown_without_relaunch_even_after_this_callback_committed()
    {
        await using var h = new Harness();
        var effect = h.Claim();
        Assert.Equal(EffectDisposition.Confirmed, (await h.Effects.ExecuteAsync(effect, default)).Disposition);
        Assert.Equal(EffectDisposition.Unknown, (await h.Effects.ReconcileAsync(effect, default)).Disposition);
        Assert.Equal(EffectDisposition.Unknown, (await h.Effects.ReconcileAsync(effect, default)).Disposition);
        Assert.Equal(1, h.Probe.Calls);
        Assert.NotNull(h.Text("SELECT facts_json FROM engine_provider"));
    }

    [Fact]
    public async Task Callback_storage_fault_is_not_swallowed_or_confirmed_and_rolls_back_all_provider_changes()
    {
        await using var h = new Harness();
        var effect = h.Claim();
        h.Core.Db.Writer.Write("test.callback-fault", scope => scope.Execute("CREATE TRIGGER callback_fault BEFORE UPDATE OF facts_json ON engine_provider BEGIN SELECT RAISE(ABORT,'fault'); END"));
        var before = h.Core.Count("engine_command_receipt");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => h.Effects.ExecuteAsync(effect, default));
        Assert.Null(h.Text("SELECT facts_json FROM engine_provider"));
        Assert.Equal("checking", h.Text("SELECT auth_state FROM engine_provider"));
        Assert.Equal(before, h.Core.Count("engine_command_receipt"));
        Assert.Equal(1, h.Probe.Calls);
    }

    [Fact]
    public async Task Genuine_invalid_descriptor_records_unknown_without_a_probe_or_launch()
    {
        await using var h = new Harness();
        h.Launcher.Agent = h.Launcher.Agent with { Command = "" };
        var result = await h.Effects.ExecuteAsync(h.Claim(), default);
        Assert.Equal(EffectDisposition.Confirmed, result.Disposition);
        Assert.Equal("unknown", h.Text("SELECT installation FROM engine_provider"));
        Assert.Equal("could_not_check", h.Text("SELECT auth_state FROM engine_provider"));
        Assert.Equal("invalid_descriptor", h.Text("SELECT reason FROM engine_provider"));
        Assert.Equal(0, h.Probe.Calls);
        Assert.Equal(0, h.Launcher.Launches);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("command")]
    [InlineData("context")]
    public async Task A_single_changed_observation_field_discards_a_late_safe_catalogue(string field)
    {
        await using var h = new Harness();
        h.Probe.Block = true;
        var pending = h.Effects.ExecuteAsync(h.Claim(), default);
        await h.Probe.Entered.Task.WaitAsync(Limit);
        h.Launcher.Agent = field switch
        {
            "name" => h.Launcher.Agent with { Name = "Renamed" },
            "command" => h.Launcher.Agent with { Command = "replacement" },
            _ => h.Launcher.Agent with { Env = new Dictionary<string, string> { ["CODEX_HOME"] = "/other" } }
        };
        h.Probe.Release.TrySetResult();
        Assert.Equal(EffectDisposition.Unknown, (await pending).Disposition);
        Assert.Null(h.Text("SELECT facts_json FROM engine_provider"));
    }

    [Fact]
    public async Task Unrelated_port_cancellation_is_not_converted_into_a_successful_failure_callback()
    {
        await using var h = new Harness();
        h.Probe.Error = new OperationCanceledException("private cancellation detail");
        var effect = h.Claim();
        var before = h.Core.Count("engine_command_receipt");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Effects.ExecuteAsync(effect, default));
        Assert.Equal(before, h.Core.Count("engine_command_receipt"));
        Assert.Null(h.Text("SELECT facts_json FROM engine_provider"));
        Assert.Equal("checking", h.Text("SELECT auth_state FROM engine_provider"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal const string Provider = "fixture/provider";
        internal string Account { get; } = "/fixture/account/one";
        internal string Target => Provider + "/" + Account;
        internal Topics Topics { get; } = new();
        internal EngineFixture Core { get; }
        internal Launcher Launcher { get; }
        internal Probe Probe { get; } = new();
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal EngineRuntime Runtime { get; }
        internal ProviderEffects Effects { get; private set; } = null!;
        private readonly SessionRegistry _hosts;
        private readonly BackgroundWork _work;
        private readonly LiveHub _hub;
        private readonly IDisposable _committed;
        private readonly IDisposable _publication;
        private readonly Channel<EngineEvent> _events = Channel.CreateUnbounded<EngineEvent>();
        private readonly Channel<bool> _changes = Channel.CreateUnbounded<bool>();

        internal Harness()
        {
            Core = new(topics: Topics);
            Launcher = new(new(Provider, "Fixture", "fixture-only-no-process", [], new Dictionary<string, string> { ["CODEX_HOME"] = Account }, VendorFamily: "codex"));
            var sessions = SessionStore.Open(Core.Db, Core.Time, "checkouts");
            var rows = RowStore.Open(Core.Db);
            _hub = new(rows);
            _work = new(Core.Time, TextWriter.Null);
            _hosts = new(_hub, sessions, Launcher, Core.Time, TextWriter.Null, _work);
            _committed = Topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => _changes.Writer.TryWrite(true));
            Runtime = EngineRuntime.Start(Core.Db, Topics, Core.Time, Core.Limits, effectsReady: Ready.Task,
                isConfiguredProvider: (provider, account) => provider == Launcher.Agent.Id && account == AccountContext.Of(Launcher.Agent, Environment.GetEnvironmentVariable),
                effectsFactory: runtime => new EffectRouter(new AdapterEffects(Core.Db, _hosts, sessions, rows))
                    .Add("probe_provider", Effects = new(Core.Db, new(Probe, "/unused/probe", Core.Time), Launcher, runtime, Core.Time)));
            _publication = Runtime.Publisher.Register(change => _events.Writer.TryWrite(change), out _);
        }

        internal CommandResult Apply(EngineCommand command, long version = 0, string? entity = null, EngineActor? actor = null) =>
            ((IEngineRuntime)Runtime).Write(scope => Runtime.Commands.Apply(scope,
                new(Guid.NewGuid(), Runtime.Machine, actor ?? new(ActorKind.Window, "owner"), entity ?? Target, version, 0, 1000), command));
        internal Effect Claim()
        {
            Assert.True(Apply(new CheckProvider()).Applied);
            return ClaimExisting();
        }
        internal Effect ClaimExisting()
        {
            var id = Text("SELECT id FROM engine_outbox WHERE kind='probe_provider' ORDER BY created_ms,id LIMIT 1")!;
            Assert.True(Apply(new ClaimEffect(), 0, id, new(ActorKind.System, "engine.worker")).Applied);
            return Core.Db.QueryFirst("SELECT id,kind,payload_json,attempt,order_key FROM engine_outbox WHERE id=$id",
                row => new Effect(row.Text(0), row.Text(1), null, null, JsonDocument.Parse(row.Text(2)).RootElement.Clone(), (int)row.Long(3), row.Text(4)), default,
                new Assembly.Application.Common.Database.SqlArg("$id", id))!;
        }
        internal void Login()
        {
            Assert.True(Apply(new StartSignIn(Provider, Account, null, null), entity: "login").Applied);
            Assert.True(Apply(new ClaimEffect(), entity: "begin_sign_in/login", actor: new(ActorKind.System, "engine.worker")).Applied);
            var receipt = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new SignInEffectData("login", "started"), EngineJson.Default.SignInEffectData), null);
            Assert.True(Apply(new CompleteEffect(receipt), 1, "begin_sign_in/login", new(ActorKind.System, "engine.worker")).Applied);
            var sink = new EngineAuthSink(Runtime, Core.Time);
            var attempt = new SignInAttemptBinding("login", Target, "begin_sign_in/login", 1);
            Assert.True(sink.Progress(attempt, "finishing", null).Applied);
            Assert.True(sink.Progress(attempt, "signed_in", null).Applied);
        }
        internal string? Text(string sql) => Core.Text(sql);
        internal async Task<EngineEvent> ProviderChange(long version)
        {
            using var deadline = new CancellationTokenSource(Limit);
            while (true)
            {
                var change = await _events.Reader.ReadAsync(deadline.Token);
                if (change.Kind == "provider.changed" && change.EntityId == Target && change.Version == version) return change;
            }
        }
        internal async Task Until(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(Limit);
            while (!condition()) await _changes.Reader.ReadAsync(deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Probe.Release.TrySetResult();
            await Runtime.DisposeAsync();
            _publication.Dispose();
            _committed.Dispose();
            await _hosts.DisposeAsync();
            await _work.DisposeAsync();
            _hub.Dispose();
            Core.Dispose();
        }
    }

    private sealed class Probe : IProviderAdapters
    {
        internal ProviderProbeResult Result = new(new(2, new("fixture-adapter", "1.2", "Fixture"), JsonDocument.Parse("{}").RootElement.Clone(), [], null),
            new("readiness", JsonDocument.Parse("[]").RootElement.Clone()), null, false, null);
        internal Exception? Error;
        internal bool Block;
        internal int Calls;
        internal AgentDescriptor? Descriptor;
        internal string? Target;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProviderProbeResult> ProbeAsync(AgentDescriptor descriptor, string target, string folder, CancellationToken ct)
        {
            Calls++;
            Descriptor = descriptor;
            Target = target;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (Error is { } error) throw error;
            return Result;
        }
        public Task<ProviderAdapterCapture?> CaptureAsync(string provider, string configurationTargetId, CancellationToken ct) => throw new NotSupportedException();
        public Task RetireAsync(ProviderAdapterCapture capture, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Launcher(AgentDescriptor agent) : IAgentLauncher
    {
        internal AgentDescriptor Agent = agent;
        internal int Launches;
        public IReadOnlyList<AgentDescriptor> Agents => [Agent];
        public bool CanRunTerminalAuthentication => false;
        public void ValidateFolder(string folder) => throw new NotSupportedException();
        public Task<string> CheckoutAsync(SessionInfo session, StageTimes stages, CancellationToken ct) => throw new NotSupportedException();
        public Task<IAgentLink> LaunchAsync(string id, StageTimes stages, CancellationToken ct) { Launches++; throw new NotSupportedException(); }
        public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment, StageTimes stages, CancellationToken ct) => LaunchAsync(descriptor.Id, stages, ct);
        public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment, IPermissionPolicy policy, StageTimes stages, CancellationToken ct) => LaunchAsync(descriptor.Id, stages, ct);
    }
}
