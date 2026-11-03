using System.Threading.Channels;
using System.Text.Json;
using Assembly.Engine.Effects;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Agents;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Execution;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthWorkerLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task One_worker_waits_for_sign_in_deadline_expires_once_and_disposes_its_only_wait()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); var commands = Commands(f);
        Start(f, commands, "operation", ""); var clock = new Clock(f.Time); using var passes = new Passes(topics);
        using var stop = new CancellationTokenSource(); var worker = new EngineWorker(f.Db, commands, f.Machine, clock, null, passes);
        var run = worker.RunAsync(stop.Token);
        try
        {
            await passes.Next();
            Assert.Equal(300_000, (await clock.Created.Reader.ReadAsync().AsTask().WaitAsync(Timeout)).TotalMilliseconds);
            Assert.Equal(1, clock.Active); Assert.Equal("starting", f.State("engine_sign_in", "operation"));
            f.Time.Advance(TimeSpan.FromMilliseconds(299_999)); worker.Wake(); await passes.Next();
            Assert.Equal("starting", f.State("engine_sign_in", "operation"));
            f.Time.Advance(TimeSpan.FromMilliseconds(1)); worker.Wake(); await passes.Until(() => f.State("engine_sign_in", "operation") == "expired");
            Assert.Equal("failed", f.State("engine_outbox", "begin_sign_in/operation"));
            Assert.Equal("pending", f.State("engine_outbox", "cancel_sign_in/operation"));
            worker.Wake(); await passes.Next(); Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='cancel_sign_in'"));
        }
        finally { await stop.CancelAsync(); await run.WaitAsync(Timeout); }
        Assert.Equal(0, clock.Active);
    }

    [Fact]
    public async Task Due_sign_ins_continue_bounded_pages_until_all_205_expire_without_provider_io()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); var commands = Commands(f);
        for (var i = 0; i < 205; i++) Start(f, commands, "op" + i, Account(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        f.Time.Advance(TimeSpan.FromSeconds(300)); using var passes = new Passes(topics); using var stop = new CancellationTokenSource();
        var worker = new EngineWorker(f.Db, commands, f.Machine, f.Time, null, passes); var run = worker.RunAsync(stop.Token);
        try
        {
            await passes.Until(() => f.Number("SELECT COUNT(*) FROM engine_sign_in WHERE state='expired'") == 205);
            Assert.Equal(205, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='cancel_sign_in' AND state='pending' AND attempt=0"));
            Assert.Equal(205, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='begin_sign_in' AND state='failed' AND attempt=0"));
            Assert.InRange(passes.Count, 3, 6);
        }
        finally { await stop.CancelAsync(); await run.WaitAsync(Timeout); }
    }

    [Fact]
    public async Task Runtime_initialization_interrupts_check_and_active_sign_in_before_any_effect_factory()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); var commands = Commands(f);
        Check(f, commands); Start(f, commands, "operation", Account("other")); var factoryCalled = false;
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, effectsFactory: _ =>
        {
            factoryCalled = true;
            Assert.Equal("could_not_check", f.Text("SELECT auth_state FROM engine_provider WHERE id='codex/'"));
            Assert.Equal("failed", f.State("engine_sign_in", "operation"));
            return new FakeEffects { Execute = (_, _) => Task.FromResult(new EffectReceipt(EffectDisposition.Unavailable, JsonSerializer.SerializeToElement((string?)null), "no_handler")) };
        });
        Assert.True(factoryCalled); Assert.Equal("interrupted", f.Text("SELECT reason FROM engine_provider WHERE id='codex/'"));
        Assert.Equal("interrupted", f.Text("SELECT reason FROM engine_sign_in WHERE id='operation'"));
        Assert.Null(f.Text("SELECT facts_json FROM engine_provider WHERE id='codex/'"));
    }

    [Fact]
    public async Task Runtime_initialization_interrupts_more_than_one_page_with_no_automatic_login()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); var commands = Commands(f);
        for (var i = 0; i < 205; i++) Start(f, commands, "op" + i, Account(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var before = f.Count("engine_outbox");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        Assert.Equal(205, f.Number("SELECT COUNT(*) FROM engine_sign_in WHERE state='failed' AND reason='interrupted'"));
        Assert.Equal(before, f.Count("engine_outbox"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE attempt<>0"));
    }

    [Fact]
    public async Task Late_old_provider_callback_after_runtime_restart_cannot_change_interrupted_version()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); var commands = Commands(f); Check(f, commands);
        var claim = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.System, "engine.worker"), "probe_provider/codex//1", 0, 0, 1000);
        Assert.True(f.Db.Writer.Write("test.auth.claim", s => commands.Apply(s, claim, new Assembly.Engine.Effects.ClaimEffect())).Applied);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits);
        using var json = System.Text.Json.JsonDocument.Parse("null");
        var prepared = EngineCommands.PrepareProviderCheck(new("installed", "could_not_check", json.RootElement, "network", new("codex/", claim.EntityId, 1, 1)));
        var result = f.Db.Writer.Write("test.auth.late", s => commands.ApplyProviderCheck(s,
            new(Guid.NewGuid(), f.Machine, new(ActorKind.System, "provider.effects"), "codex/", 1, 0, 1000), prepared));
        Assert.False(result.Applied); Assert.Equal(2, f.Number("SELECT version FROM engine_provider WHERE id='codex/'"));
        Assert.Equal("interrupted", f.Text("SELECT reason FROM engine_provider WHERE id='codex/'"));
    }

    [Fact]
    public async Task Initialization_fault_rolls_back_all_interruptions_before_effects_and_worker_exist()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics); var commands = Commands(f); Check(f, commands); Start(f, commands, "operation", Account("other"));
        f.Db.Writer.Write("test.auth.initialize-fault", s => s.Execute("CREATE TRIGGER reject_interrupt BEFORE UPDATE ON engine_sign_in WHEN NEW.reason='interrupted' BEGIN SELECT RAISE(ABORT,'interrupt fault'); END"));
        var events = f.Count("engine_event"); var receipts = f.Count("engine_command_receipt"); var factoryCalled = false;
        EngineRuntime? attempted = null;
        var error = Record.Exception(() => attempted = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, effectsFactory: _ => { factoryCalled = true; return new FakeEffects { Execute = (_, _) => Task.FromResult(new EffectReceipt(EffectDisposition.Unavailable, JsonSerializer.SerializeToElement((string?)null), "no_handler")) }; }));
        if (attempted is not null) await attempted.DisposeAsync();
        Assert.IsType<SqliteException>(error);
        Assert.False(factoryCalled); Assert.Equal(events, f.Count("engine_event")); Assert.Equal(receipts, f.Count("engine_command_receipt"));
        Assert.Equal("checking", f.Text("SELECT auth_state FROM engine_provider WHERE id='codex/'")); Assert.Equal("starting", f.State("engine_sign_in", "operation"));
    }

    private static EngineCommands Commands(EngineFixture f) => new(f.Machine, f.Limits, isConfiguredProvider: (p, _) => p == "codex");
    private static string Account(string home) => AccountContext.Of(
        new("codex", "Codex", "unused", [], new Dictionary<string, string> { ["CODEX_HOME"] = home }, VendorFamily: "codex"), _ => null);
    private static void Start(EngineFixture f, EngineCommands commands, string id, string account) => Assert.True(f.Db.Writer.Write("test.auth.start", s => commands.Apply(s,
        new(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), id, 0, 0, f.Time.GetUtcNow().ToUnixTimeMilliseconds()), new StartSignIn("codex", account, null, null))).Applied);
    private static void Check(EngineFixture f, EngineCommands commands) => Assert.True(f.Db.Writer.Write("test.auth.check", s => commands.Apply(s,
        new(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), "codex/", 0, 0, 1000), new CheckProvider())).Applied);

    private sealed class Passes : IEngineRetention, IDisposable
    {
        private readonly Channel<bool> _passes = Channel.CreateUnbounded<bool>();
        private readonly IDisposable _subscription;
        public int Count { get; private set; }
        public Passes(Topics topics) => _subscription = topics.Subscribe("test.auth.pass", null, _ => { }, (_, _) => _passes.Writer.TryWrite(true));
        public void Apply(IWriteScope scope, long now) { Count++; scope.Publish("test.auth.pass", null, (long)Count, EngineJson.Default.Int64); }
        public long? NextDeadlineMs(IReadScope scope, long now) => null;
        public async Task Next() => await _passes.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        public async Task Until(Func<bool> done) { using var stop = new CancellationTokenSource(Timeout); while (!done()) await _passes.Reader.ReadAsync(stop.Token); }
        public void Dispose() => _subscription.Dispose();
    }
    private sealed class Clock(FakeTimeProvider time) : TimeProvider
    {
        private int _active;
        public int Active => Volatile.Read(ref _active);
        public Channel<TimeSpan> Created { get; } = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _active); var timer = new Owned(time.CreateTimer(callback, state, dueTime, period), () => Interlocked.Decrement(ref _active));
            Created.Writer.TryWrite(dueTime); return timer;
        }
        private sealed class Owned(ITimer timer, Action stopped) : ITimer
        {
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { timer.Dispose(); stopped(); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
