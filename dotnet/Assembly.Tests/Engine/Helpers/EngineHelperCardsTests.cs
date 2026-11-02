using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mirror;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Helpers;
using Assembly.Tests.Infrastructure.Remote;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Helpers;

public sealed class EngineHelperCardsTests
{
    [Theory]
    [InlineData("finished", null, null)]
    [InlineData("cancelled", "cancelled", null)]
    [InlineData("failed", null, "Actual failure")]
    public async Task Accepted_helper_start_message_and_terminal_keep_the_existing_card_and_parent_working(
        string terminal, string? stop, string? error)
    {
        await using var f = new Fixture();
        using var live = f.Hub.Subscribe(f.Parent.SessionId, 0);
        var helper = f.Store.Helper(f.Parent, "codex", "Exact helper title", f.Parent.SessionId);
        var started = await NextCard(live);
        Assert.Equal("subagent:" + helper.SessionId, started.ItemId);
        var payload = Card(started);
        Assert.Equal(new(helper.SessionId, "Exact helper title", "Session (codex)", "running", null, null,
            true, null, "codex", true), payload);
        Assert.True(f.Registry.StartedWorking(f.Parent.SessionId));
        f.Begin(helper.SessionId, "turn-one");
        f.End(helper.SessionId, terminal, error);
        var ended = Card(await NextCard(live));
        Assert.Equal("idle", ended.State); Assert.False(ended.CanStop);
        Assert.Equal(stop, ended.StopReason); Assert.Equal(error, ended.Error);
        Assert.False(f.Registry.StartedWorking(f.Parent.SessionId));

        f.Begin(helper.SessionId, "turn-two");
        var messaged = Card(await NextCard(live));
        Assert.Equal("running", messaged.State); Assert.True(messaged.CanStop);
        Assert.Null(messaged.StopReason); Assert.Null(messaged.Error);
        Assert.True(f.Registry.StartedWorking(f.Parent.SessionId));
        f.End(helper.SessionId, "finished");
        Assert.Equal("idle", Card(await NextCard(live)).State);
        await f.Cards.DisposeAsync();
        Assert.Equal(4, f.Rows.RowsAfter(f.Parent.SessionId, 0).Count(row => row.Kind == RowKind.Subagent));
        Assert.Equal(0, f.Launcher.ProviderCalls);
    }

    [Fact]
    public async Task A_rolled_back_start_never_leaks_work_or_a_card()
    {
        await using var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => f.Engine.Db.Writer.Write<int>("test.rollback", scope =>
        {
            var result = f.Runtime.Commands.Apply(scope, new(Guid.NewGuid(), f.Runtime.Machine,
                new(ActorKind.Session, f.Parent.SessionId), "rolled-back", 0, 0, 1000),
                new StartSession("codex", null, "/folder", "Rolled back", f.Parent.SessionId, null, null));
            Assert.True(result.Applied);
            Assert.False(f.Cards.Working(f.Parent.SessionId));
            throw new InvalidOperationException("rollback after capture");
        }));
        await f.Cards.DisposeAsync();
        Assert.False(f.Registry.StartedWorking(f.Parent.SessionId));
        Assert.Empty(f.Rows.RowsAfter(f.Parent.SessionId, 0));
        Assert.Null(f.Store.Get("rolled-back"));
        Assert.Equal(0, f.Engine.Number("SELECT COUNT(*) FROM engine_session WHERE id='rolled-back'"));
    }

    [Fact]
    public async Task Cold_seed_and_repeated_committed_notifications_reuse_the_existing_card()
    {
        await using var f = new Fixture(start: false);
        var helper = f.Store.Helper(f.Parent, "codex", "Kept title", f.Parent.SessionId);
        f.Begin(helper.SessionId, "before-restart"); f.End(helper.SessionId, "failed", "Recorded failure");
        f.Registry.Helpers.Row.Show(helper, "idle", error: "Recorded failure");
        var before = f.Rows.LastSeq(f.Parent.SessionId);
        f.Cards.Start(f.Registry.Helpers.Row);
        f.Cards.Refresh(helper.SessionId); f.Cards.Refresh(helper.SessionId);
        await f.Cards.DisposeAsync();
        Assert.False(f.Registry.StartedWorking(f.Parent.SessionId));
        Assert.Equal(before, f.Rows.LastSeq(f.Parent.SessionId));
        Assert.Equal("Recorded failure", Card(Assert.Single(f.Rows.RowsAfter(f.Parent.SessionId, 0))).Error);
    }

    [Fact]
    public async Task Copied_remote_terminal_supplies_details_but_does_not_override_committed_engine_activity()
    {
        await using var f = new Fixture();
        using var live = f.Hub.Subscribe(f.Parent.SessionId, 0);
        f.Mirror("running", 1);
        Assert.Equal("running", Card(await NextCard(live)).State);
        var converter = new RowConverter(); long seq = 0;
        foreach (var e in new AgentEvent[] { new TurnStarted(1000, "remote-run", "task"), new TurnFailed(1100, "Remote failure", "failed") })
            foreach (var row in converter.Apply(e).Rows)
            {
                Assert.True(f.Hub.PublishCopy(new("remote-child", ++seq, row.RunId, row.ItemId, row.Kind,
                    row.Payload, row.CreatedAt, PrevSeq: seq - 1)));
            }
        f.Cards.Refresh("remote-child");
        Assert.True(f.Registry.StartedWorking(f.Parent.SessionId));
        f.Mirror("idle", 2);
        var ended = Card(await NextCard(live));
        Assert.Equal("idle", ended.State); Assert.Equal("Remote failure", ended.Error);
        Assert.False(ended.CanStop); Assert.False(f.Registry.StartedWorking(f.Parent.SessionId));
        f.Cards.Refresh("remote-child");
        await f.Cards.DisposeAsync();
        Assert.Equal(2, f.Rows.RowsAfter(f.Parent.SessionId, 0).Count);
    }

    [Fact]
    public async Task A_remote_owned_parents_copied_helper_card_remains_visible_here()
    {
        await using var f = new Fixture();
        f.Store.Add("remote-parent", "codex", "repo", "workbox-test");
        using var live = f.Hub.Subscribe("remote-parent", 0);
        var payload = new SubagentPayload("local-child", "Local child seen by remote parent", "Session (codex)",
            "running", null, null, true, null, "codex", true);
        Assert.True(f.Hub.PublishCopy(new("remote-parent", 1, "remote-parent-run", "subagent:local-child", RowKind.Subagent,
            JsonSerializer.SerializeToElement(payload, RowJsonContext.Rows.SubagentPayload), 1000, PrevSeq: 0)));
        Assert.Equal(payload, Card(await NextCard(live)));
        f.Cards.Refresh("local-child");
        await f.Cards.DisposeAsync();
        Assert.Equal(payload, Card(Assert.Single(f.Rows.RowsAfter("remote-parent", 0))));
    }

    private static SubagentPayload Card(StoredRow row) => row.Payload.Deserialize(RowJsonContext.Rows.SubagentPayload)!;
    private static async Task<StoredRow> NextCard(LiveSubscription live)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        StoredRow row;
        do { row = await live.Rows.ReadAsync(timeout.Token); } while (row.Kind != RowKind.Subagent);
        return row;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public EngineFixture Engine { get; } = new();
        public RelayTestLauncher Launcher { get; } = new();
        public RowStore Rows { get; }
        public SessionStore Store { get; }
        public LiveHub Hub { get; }
        public SessionRegistry Registry { get; }
        public EngineRuntime Runtime { get; }
        public EngineHelperCards Cards { get; }
        public SessionInfo Parent { get; }
        private readonly BackgroundWork _work;
        private readonly EngineFeed _feed;
        private readonly Dictionary<string, RowConverter> _converters = [];

        public Fixture(bool start = true)
        {
            Rows = RowStore.Open(Engine.Db);
            var summaries = SessionSummaries.Load(Rows);
            Store = SessionStore.Open(Engine.Db, Engine.Time, "/checkouts", summaries);
            Parent = Store.Create("codex", "repo");
            Hub = new(Rows, summaries: summaries);
            Cards = new(Engine.Db, Engine.Time, TextWriter.Null);
            Runtime = EngineRuntime.Start(Engine.Db, new Topics(), Engine.Time, Engine.Limits,
                cutover: new(Engine.Db, Store, Launcher, "/checkouts", Engine.Time, helperCards: Cards));
            _feed = new(Runtime, TextWriter.Null, Engine.Time, Cards);
            _work = new(Engine.Time, TextWriter.Null);
            Registry = new(Hub, Store, Launcher, Engine.Time, TextWriter.Null, _work, feed: _feed);
            Registry.BindHelperWorking(Cards.Working, Cards.Refresh);
            if (start) Cards.Start(Registry.Helpers.Row);
        }

        public void Begin(string child, string run)
        {
            if (Engine.State("engine_session", child) == "starting")
                Engine.Db.Writer.Write("test.start-completed", scope => Runtime.SubmitCurrent(scope, "session", child,
                    new SetSessionState("idle", null, null), 0));
            var queued = Engine.Db.Writer.Write("test.accept-message", scope => Runtime.Commands.Apply(scope,
                new(Guid.NewGuid(), Runtime.Machine, new(ActorKind.Session, Parent.SessionId), Guid.NewGuid().ToString(), 0, 0, 1000),
                new SendMessage(Parent.SessionId, child, "Accepted helper message", "prompt")));
            Assert.True(queued.Applied);
            var e = new TurnStarted(1000, run, "Accepted helper message");
            _converters.TryAdd(child, new RowConverter());
            var observed = _feed.Capture(child)!;
            Hub.Publish(child, _converters[child].Apply(e), scope => _feed.ObserveBatch(scope, observed, "acp-" + child, [e]));
        }

        public void End(string child, string terminal, string? error = null)
        {
            AgentEvent e = terminal == "failed" ? new TurnFailed(1100, error!, "failed") :
                new TurnEnded(1100, terminal == "cancelled" ? "cancelled" : "end_turn", null);
            var observed = _feed.Capture(child)!;
            Hub.Publish(child, _converters[child].Apply(e), scope => _feed.ObserveBatch(scope, observed, "acp-" + child, [e]));
        }

        public void Mirror(string state, long version) => Engine.Db.Writer.Write("test.remote-snapshot", scope =>
        {
            MirrorApply.Open(scope, "remote-owner", "workbox-test", 1000);
            MirrorApply.BeginSnapshot(scope, "remote-owner", 1000);
            var row = JsonSerializer.SerializeToElement(new
            {
                entityKind = "session",
                id = "remote-child",
                machine = "remote-owner",
                version,
                provider = "codex",
                title = "Remote title",
                parentId = Parent.SessionId,
                holder = "local",
                state,
                activityState = state,
                epoch = 0
            });
            MirrorApply.SnapshotPage(scope, "remote-owner", [row]);
            MirrorApply.EndSnapshot(scope, "remote-owner", 0, null, 1000);
            Cards.Mirrored(scope, "remote-owner");
            return 0;
        });

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync(); await Cards.DisposeAsync();
            await Registry.DisposeAsync(); await _work.DisposeAsync(); Hub.Dispose(); Engine.Dispose();
        }
    }
}
