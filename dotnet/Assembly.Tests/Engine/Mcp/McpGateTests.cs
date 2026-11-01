using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Effects;
using Assembly.Engine.Hosting;
using Assembly.Engine.Mcp;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Mcp;

public sealed class McpGateTests
{
    private static McpGate Gate(EngineRuntime runtime, EngineFixture f) => new(runtime, new(() => 1, () => 5), f.Time);

    [Fact]
    public async Task Refused_second_command_rolls_back_the_first()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession();
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 1, () => 5));
        string[] tables = ["engine_session", "engine_message", "engine_event", "engine_outbox", "engine_command_receipt"];
        var before = tables.Select(f.Count).ToArray();
        var publications = 0;
        using var subscription = topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        var error = Assert.Throws<McpRefused>(() => Gate(runtime, f).Write<int>(new("s", 0), w =>
        {
            w.Apply("child", 0, 0, new StartSession("codex", "m", "/f", "Child", "s", null, null));
            w.Apply("msg", 0, w.EpochOf("child"), new SendMessage("s", "child", "", "prompt"));
            return 0;
        }));
        Assert.Equal("bad_args", error.Refusal.Code);
        Assert.Equal(before, tables.Select(f.Count).ToArray());
        Assert.Equal(0, publications);
    }

    [Theory]
    [InlineData("idle", 1, "stale_epoch")]
    [InlineData("ended", 0, "session_gone")]
    [InlineData("lost", 0, "session_gone")]
    [InlineData("handing_off", 0, "handing_off")]
    public async Task Invalid_caller_is_refused_before_body(string state, long epoch, string code)
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession(state: state, epoch: epoch);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 1, () => 5));
        var gate = Gate(runtime, f);
        Assert.Equal(code, Assert.Throws<McpRefused>(() => gate.Write<int>(new("s", 0), _ => throw new InvalidOperationException())).Refusal.Code);
        Assert.Equal(code, Assert.Throws<McpRefused>(() => gate.Read<int>(new("s", 0), (_, _) => throw new InvalidOperationException(), default)).Refusal.Code);
        Assert.Equal("session_gone", Assert.Throws<McpRefused>(() => gate.Write(new("missing", 0), _ => 0)).Refusal.Code);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("waiting")]
    public async Task Caller_row_has_depth_run_step_and_attempt(string stepState)
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession("owner");
        f.SeedRun(ownerSessionId: "owner");
        f.SeedSession("child", "running", "owner", "r", "claude");
        f.SeedStep("st", state: stepState, sessionId: "child", attempt: 2);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 1, () => 5));
        var expected = new CallerRow("child", 0, "running", "owner", 1, "r", "st", 2, "claude", "model", "/test", "Test");
        Assert.Equal(expected, Gate(runtime, f).Read(new("child", 0), (row, _) => row, default));
        Assert.Equal(expected, Gate(runtime, f).Write(new("child", 0), w => w.Caller));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_read_disposes_its_snapshot(bool fail)
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 1, () => 5));
        IEngineRuntime port = runtime;
        IReadScope? captured = null;
        long Read() => port.Read(scope =>
        {
            captured = scope;
            var result = scope.QueryFirst("SELECT 1", row => row.Long(0));
            if (fail) throw new InvalidOperationException("read-body");
            return result;
        }, default);
        if (fail) Assert.Throws<InvalidOperationException>(() => Read());
        else Assert.Equal(1L, Read());
        Assert.Throws<ObjectDisposedException>(() => captured!.QueryFirst("SELECT 1", row => row.Long(0)));
    }

    [Fact]
    public async Task Cancelled_read_does_not_enter_its_body()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 1, () => 5));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var entered = false;
        Assert.ThrowsAny<OperationCanceledException>(() => ((IEngineRuntime)runtime).Read(_ => { entered = true; return 1; }, cancelled.Token));
        Assert.False(entered);
    }

    [Fact]
    public async Task Effects_observe_the_whole_committed_batch()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = new FakeEffects
        {
            Execute = (effect, _) =>
        {
            try
            {
                using var snapshot = f.Db.OpenSnapshot(default);
                Assert.Equal(1L, snapshot.QueryFirst("SELECT COUNT(*) FROM engine_session WHERE id='child'", row => row.Long(0)));
                Assert.Equal(1L, snapshot.QueryFirst("SELECT COUNT(*) FROM engine_message WHERE id='msg'", row => row.Long(0)));
                observed.TrySetResult();
            }
            catch (Exception error) { observed.TrySetException(error); }
            return Task.FromResult(new EffectReceipt(EffectDisposition.Confirmed,
                effect.Kind == "start"
                    ? JsonSerializer.SerializeToElement(new SessionEffectData("idle", "provider-child", null), EngineJson.Default.SessionEffectData)
                    : JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", null, null), EngineJson.Default.MessageEffectData), null));
        }
        };
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 1, () => 5), effects);
        Gate(runtime, f).Write(new("s", 0), w =>
        {
            w.Apply("child", 0, 0, new StartSession("codex", "m", "/f", "Child", "s", null, null));
            return w.Apply("msg", 0, 0, new SendMessage("s", "child", "Do it", "prompt"));
        });
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Gate_read_keeps_caller_and_body_in_one_snapshot()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession(epoch: 7);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 1, () => 5));
        Gate(runtime, f).Read(new("s", 7), (caller, scope) =>
        {
            f.Db.Writer.Write("test.epoch", write => write.Execute("UPDATE engine_session SET epoch=8 WHERE id='s'"));
            Assert.Equal(7, caller.Epoch);
            Assert.Equal(7, scope.QueryFirst("SELECT epoch FROM engine_session WHERE id='s'", row => row.Long(0)));
            return 0;
        }, default);
        Assert.Equal(8, f.Number("SELECT epoch FROM engine_session WHERE id='s'"));
    }

    [Fact]
    public async Task Child_limit_refusal_uses_the_current_limit()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession();
        for (var i = 0; i < 5; i++) f.SeedSession($"child-{i}", parentId: "s");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 1, () => 5));
        var error = Assert.Throws<McpRefused>(() => Gate(runtime, f).Write(new("s", 0), w =>
            w.Apply("sixth", 0, 0, new StartSession("codex", "m", "/f", "Child", "s", null, null))));
        Assert.Equal("child_limit", error.Refusal.Code);
        Assert.Equal("5", error.Refusal.Values!["limit"]);
    }

    [Fact]
    public async Task Versions_and_epochs_are_read_in_the_write_scope_and_kinds_are_allowlisted()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession(epoch: 7);
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 1, () => 5));
        Gate(runtime, f).Write(new("s", 7), w =>
        {
            Assert.Equal(7, w.EpochOf("s"));
            Assert.Equal(0, w.VersionOf("session", "absent"));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.VersionOf("session; DROP TABLE engine_session", "s"));
            Assert.Equal("not_reachable", Assert.Throws<McpRefused>(() => w.EpochOf("absent")).Refusal.Code);
            w.Apply("child", 0, 0, new StartSession("codex", "m", "/f", "Child", "s", null, null));
            Assert.Equal(1, w.VersionOf("session", "child"));
            return 0;
        });
    }
}
