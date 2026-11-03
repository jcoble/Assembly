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
using Assembly.Engine.Effects;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_receipt_keeps_helper_working_while_the_initial_prompt_is_in_flight(bool failed)
    {
        await using var f = new Fixture();
        using var live = f.Hub.Subscribe(f.Parent.SessionId, 0);
        var helper = f.Store.Helper(f.Parent, "codex", "Initial prompt", f.Parent.SessionId);
        Assert.Equal("running", Card(await NextCard(live)).State);
        var queued = f.Engine.Db.Writer.Write("test.initial-prompt", scope => f.Runtime.Commands.Apply(scope,
            new(Guid.NewGuid(), f.Runtime.Machine, new(ActorKind.Session, f.Parent.SessionId), "initial-prompt", 0, 0, 1000),
            new SendMessage(f.Parent.SessionId, helper.SessionId, "Do the task", "prompt")));
        Assert.True(queued.Applied);

        f.CompleteEffect(helper.SessionId, "start", new(EffectDisposition.Confirmed,
            JsonSerializer.SerializeToElement(new SessionEffectData("idle", "acp-" + helper.SessionId, null), EngineJson.Default.SessionEffectData), null));

        Assert.Equal("idle", f.Engine.State("engine_session", helper.SessionId));
        Assert.Equal(1, f.Engine.Number("SELECT in_flight FROM engine_message WHERE id='initial-prompt'"));
        Assert.True(f.Registry.StartedWorking(f.Parent.SessionId));
        if (failed)
            f.CompleteEffect(helper.SessionId, "send", new(EffectDisposition.Failed,
                JsonSerializer.SerializeToElement((string?)null), "Prompt failed before insertion"));
        else
        {
            f.StartTurn(helper.SessionId, "initial-turn");
            f.CompleteEffect(helper.SessionId, "send", new(EffectDisposition.Confirmed,
                JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", "initial-turn", null), EngineJson.Default.MessageEffectData), null));
            Assert.True(f.Registry.StartedWorking(f.Parent.SessionId));
            f.End(helper.SessionId, "finished");
        }
        Assert.False(f.Registry.StartedWorking(f.Parent.SessionId));
        Assert.Equal("idle", Card(await NextCard(live)).State);
        await f.Cards.DisposeAsync();
        Assert.Equal(new[] { "running", "idle" }, f.Rows.RowsAfter(f.Parent.SessionId, 0)
            .Where(row => row.Kind == RowKind.Subagent).Select(row => Card(row).State));
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mirrored_parent_exposes_its_current_helpers_without_writing_its_transcript(bool separateOwner)
    {
        await using var f = new Fixture();
        f.Store.Add("remote-parent", "codex", "parent-repository", "parent-box");
        var parent = MirrorSession("parent-owner", "remote-parent", null, "idle");
        var owner = separateOwner ? "child-owner" : "parent-owner";
        var name = separateOwner ? "child-box" : "parent-box";
        var child = MirrorSession(owner, "mirrored-child", "remote-parent", "running");
        if (separateOwner)
        {
            f.Snapshot("parent-owner", "parent-box", parent);
            f.ProveChild("parent-owner", "remote-parent", owner, "mirrored-child");
            f.Snapshot(owner, name, child);
        }
        else f.Snapshot(owner, name, parent, child);

        Assert.True(f.Cards.Working("remote-parent"));
        var display = Assert.IsType<SessionInfo>(f.Store.Get("mirrored-child"));
        Assert.Equal(name, display.Machine); Assert.Equal("remote-parent", display.ParentSessionId);
        Assert.Equal("parent-repository", display.RepoUrl);
        Assert.True(display.Helper);
        Assert.Equal(0, f.Engine.Number("SELECT COUNT(*) FROM engine_session WHERE id='mirrored-child' AND state<>'foreign'"));
        Assert.Empty(f.Rows.RowsAfter("remote-parent", 0));

        var idle = MirrorSession(owner, "mirrored-child", "remote-parent", "idle", version: 2);
        f.Snapshot(owner, name, separateOwner ? [idle] : [parent, idle]);
        Assert.False(f.Cards.Working("remote-parent"));
        Assert.Empty(f.Rows.RowsAfter("remote-parent", 0));
    }

    [Fact]
    public async Task An_accepted_local_child_of_a_proven_remote_parent_is_visible_without_local_parent_admission()
    {
        await using var f = new Fixture();
        f.Store.Add("remote-parent", "codex", "remote-repository", "parent-box");
        f.Snapshot("parent-owner", "parent-box", MirrorSession("parent-owner", "remote-parent", null, "idle"));
        var ancestry = f.Runtime.Commands.PrepareForeignAncestry(new("parent-owner", "remote-parent",
            [new("remote-parent", "parent-owner", "codex", "Remote parent", null)]));
        f.Engine.Db.Writer.Write("test.peer-start", scope =>
        {
            Assert.True(f.Runtime.Commands.ApplyForeignAncestryBatch(scope, ancestry).Applied);
            var result = f.Runtime.Commands.Apply(scope,
                new(Guid.NewGuid(), f.Runtime.Machine, new(ActorKind.System, "machine:parent-owner"), "local-child", 0, 0, 1000),
                new StartSession("codex", null, "/local-work", "Local child", "remote-parent", null, null));
            Assert.True(result.Applied, result.Error);
            return 0;
        });
        Assert.True(f.Cards.Working("remote-parent"));
        Assert.Equal("foreign", f.Engine.State("engine_session", "remote-parent"));
        Assert.Null(f.Store.Get("local-child")!.Machine);
        Assert.Empty(f.Rows.RowsAfter("remote-parent", 0));
        Assert.Equal(0, f.Launcher.ProviderCalls);
    }

    [Fact]
    public async Task Missing_or_retired_parent_authority_removes_visibility_and_a_new_snapshot_restores_it()
    {
        await using var f = new Fixture();
        f.Store.Add("remote-parent", "codex", "repo", "parent-box");
        var child = MirrorSession("child-owner", "mirrored-child", "remote-parent", "running");
        f.ProveChild("parent-owner", "remote-parent", "child-owner", "mirrored-child");
        f.Snapshot("child-owner", "child-box", child);
        Assert.False(f.Cards.Working("remote-parent")); Assert.Null(f.Store.Get("mirrored-child"));
        var parent = MirrorSession("parent-owner", "remote-parent", null, "idle");
        f.Snapshot("parent-owner", "parent-box", parent);
        Assert.True(f.Cards.Working("remote-parent")); Assert.NotNull(f.Store.Get("mirrored-child"));
        f.Snapshot("parent-owner", "parent-box");
        Assert.False(f.Cards.Working("remote-parent"));
        f.Snapshot("parent-owner", "parent-box", parent);
        Assert.True(f.Cards.Working("remote-parent"));
        // A replacement machine under the saved name retires the old source, not its historical rows.
        f.Snapshot("replacement-owner", "parent-box");
        Assert.False(f.Cards.Working("remote-parent"));
        Assert.Empty(f.Rows.RowsAfter("remote-parent", 0));
    }

    [Theory]
    [InlineData("wrong-facade")]
    [InlineData("foreign")]
    [InlineData("wrong-owner")]
    public async Task A_saved_name_or_foreign_parent_copy_is_not_parent_authority(string invalid)
    {
        await using var f = new Fixture();
        f.Store.Add("remote-parent", "codex", "repo", invalid == "wrong-facade" ? "other-box" : "parent-box");
        f.ProveChild("parent-owner", "remote-parent", "child-owner", "mirrored-child");
        var parent = MirrorSession(invalid == "wrong-owner" ? "forged-owner" : "parent-owner",
            "remote-parent", null, invalid == "foreign" ? "foreign" : "idle");
        if (invalid == "wrong-owner")
        {
            var error = Assert.Throws<ArgumentException>(() => f.Snapshot("parent-owner", "parent-box", parent));
            Assert.Equal("Invalid mirror row", error.Message);
            Assert.Equal(0, f.Engine.Number("SELECT COUNT(*) FROM engine_mirror WHERE machine='parent-owner'"));
        }
        else f.Snapshot("parent-owner", "parent-box", parent);
        f.Snapshot("child-owner", "child-box", MirrorSession("child-owner", "mirrored-child", "remote-parent", "running"));
        Assert.False(f.Cards.Working("remote-parent")); Assert.Null(f.Store.Get("mirrored-child"));
        Assert.Empty(f.Rows.RowsAfter("remote-parent", 0));
    }

    [Fact]
    public async Task A_mixed_owner_child_cannot_infer_its_parent_owner_from_a_bare_or_colliding_id()
    {
        await using var f = new Fixture();
        f.Store.Add("same-parent-id", "codex", "repo", "parent-box");
        f.Snapshot("parent-owner", "parent-box", MirrorSession("parent-owner", "same-parent-id", null, "idle"));
        var child = MirrorSession("child-owner", "mirrored-child", "same-parent-id", "running");
        f.Snapshot("child-owner", "child-box", child);
        Assert.False(f.Cards.Working("same-parent-id")); Assert.Null(f.Store.Get("mirrored-child"));
        f.Snapshot("child-owner", "child-box", MirrorSession("child-owner", "same-parent-id", null, "idle"), child);
        Assert.False(f.Cards.Working("same-parent-id")); Assert.Null(f.Store.Get("mirrored-child"));
        Assert.Empty(f.Rows.RowsAfter("same-parent-id", 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_explicit_parent_owner_is_used_without_guessing_and_cannot_be_overridden_by_legacy_proof(bool contradictsProof)
    {
        await using var f = new Fixture();
        f.Store.Add("remote-parent", "codex", "repo", "parent-box");
        f.Snapshot("parent-owner", "parent-box", MirrorSession("parent-owner", "remote-parent", null, "idle"));
        if (contradictsProof) f.ProveChild("parent-owner", "remote-parent", "child-owner", "mirrored-child");
        f.Snapshot("child-owner", "child-box", MirrorSession("child-owner", "mirrored-child", "remote-parent", "running",
            parentMachine: contradictsProof ? "different-owner" : "parent-owner"));
        Assert.Equal(!contradictsProof, f.Cards.Working("remote-parent"));
        Assert.Equal(!contradictsProof, f.Store.Get("mirrored-child") is not null);
        Assert.Equal(0, f.Engine.Number("SELECT COUNT(*) FROM engine_session WHERE id='mirrored-child' AND state<>'foreign'"));
        Assert.Empty(f.Rows.RowsAfter("remote-parent", 0));
    }

    [Fact]
    public async Task A_conflicting_child_facade_refuses_and_rolled_back_mirror_never_publishes_visibility()
    {
        await using var f = new Fixture();
        f.Store.Add("remote-parent", "codex", "repo", "parent-box");
        f.Store.Add("mirrored-child", "codex", "repo", "other-box", "remote-parent", helper: true);
        f.ProveChild("parent-owner", "remote-parent", "child-owner", "mirrored-child");
        f.Snapshot("parent-owner", "parent-box", MirrorSession("parent-owner", "remote-parent", null, "idle"));
        Assert.Throws<InvalidOperationException>(() => f.Snapshot("child-owner", "child-box",
            MirrorSession("child-owner", "mirrored-child", "remote-parent", "running")));
        Assert.False(f.Cards.Working("remote-parent"));
        Assert.Equal("other-box", f.Store.Get("mirrored-child")!.Machine);
        Assert.Equal(0, f.Engine.Number("SELECT COUNT(*) FROM engine_mirror WHERE machine='child-owner'"));
    }

    // SOURCE-DERIVED display DTOs, passed through the actual mirror writer; no provider capture or Engine admission.
    private static JsonElement MirrorSession(string machine, string id, string? parentId, string state, long version = 1,
        string? parentMachine = null) =>
        JsonSerializer.SerializeToElement(new
        {
            entityKind = "session",
            machine,
            id,
            parentId,
            parentMachine,
            state,
            version,
            provider = "codex",
            folder = "/remote-work",
            updatedMs = 1000,
            title = "Remote title",
            holder = "local",
            activityState = state,
            epoch = 0
        });

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
            Cards = new(Engine.Db, Engine.Time, TextWriter.Null, Store);
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
            var queued = Engine.Db.Writer.Write("test.accept-message", scope => Runtime.Commands.Apply(scope,
                new(Guid.NewGuid(), Runtime.Machine, new(ActorKind.Session, Parent.SessionId), Guid.NewGuid().ToString(), 0, 0, 1000),
                new SendMessage(Parent.SessionId, child, "Accepted helper message", "prompt")));
            Assert.True(queued.Applied);
            if (Engine.State("engine_session", child) == "starting")
                CompleteEffect(child, "start", new(EffectDisposition.Confirmed,
                    JsonSerializer.SerializeToElement(new SessionEffectData("idle", "acp-" + child, null), EngineJson.Default.SessionEffectData), null));
            StartTurn(child, run);
            CompleteEffect(child, "send", new(EffectDisposition.Confirmed,
                JsonSerializer.SerializeToElement(new MessageEffectData("next_turn", run, null), EngineJson.Default.MessageEffectData), null));
        }

        public void StartTurn(string child, string run)
        {
            var e = new TurnStarted(1000, run, "Accepted helper message");
            _converters.TryAdd(child, new RowConverter());
            var observed = _feed.Capture(child)!;
            Hub.Publish(child, _converters[child].Apply(e), scope => _feed.ObserveBatch(scope, observed, "acp-" + child, [e]));
        }

        public void CompleteEffect(string child, string kind, EffectReceipt receipt)
        {
            Engine.Db.Writer.Write("test.effect-complete", scope =>
            {
                var effect = scope.QueryFirst("SELECT id,version FROM engine_outbox WHERE session_id=$child AND kind=$kind AND state='pending'",
                    r => (Id: r.Text(0), Version: r.Long(1)), new SqlArg("$child", child), new("$kind", kind));
                var context = new CommandContext(Guid.NewGuid(), Runtime.Machine, new(ActorKind.System, "engine.worker"),
                    effect.Id, effect.Version, 0, 1000);
                var claimed = Runtime.Commands.Apply(scope, context, new ClaimEffect());
                Assert.True(claimed.Applied, claimed.Error);
                var completed = Runtime.Commands.Apply(scope, context with { CommandId = Guid.NewGuid(), ExpectedVersion = effect.Version + 1 },
                    new CompleteEffect(receipt));
                Assert.True(completed.Applied, completed.Error);
                Cards.Observe(scope, child);
                return 0;
            });
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
            var ancestry = Runtime.Commands.PrepareForeignAncestry(new("remote-owner", "remote-child",
                [new(Parent.SessionId, Runtime.Machine, Parent.Agent, Parent.Title ?? "", Parent.ParentSessionId),
                    new("remote-child", "remote-owner", "codex", "Remote title", Parent.SessionId)]));
            Assert.True(Runtime.Commands.ApplyForeignAncestryBatch(scope, ancestry).Applied);
            MirrorApply.Open(scope, "remote-owner", "workbox-test", 1000);
            MirrorApply.BeginSnapshot(scope, "remote-owner", 1000);
            var row = JsonSerializer.SerializeToElement(new
            {
                entityKind = "session",
                id = "remote-child",
                machine = "remote-owner",
                version,
                provider = "codex",
                // SOURCE-DERIVED metadata matching the owning session display DTO, not a provider capture.
                folder = "/remote-work",
                updatedMs = 1000,
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

        public void ProveChild(string parentOwner, string parent, string childOwner, string child)
        {
            var ancestry = Runtime.Commands.PrepareForeignAncestry(new(childOwner, child,
                [new(parent, parentOwner, "codex", "Remote title", null),
                    new(child, childOwner, "codex", "Remote title", parent)]));
            Assert.True(Engine.Db.Writer.Write("test.proven-child", scope =>
                Runtime.Commands.ApplyForeignAncestryBatch(scope, ancestry)).Applied);
        }

        public void Snapshot(string machine, string name, params JsonElement[] rows) => Engine.Db.Writer.Write("test.helper-snapshot", scope =>
        {
            MirrorApply.Open(scope, machine, name, 1000);
            MirrorApply.BeginSnapshot(scope, machine, 1000);
            MirrorApply.SnapshotPage(scope, machine, rows);
            MirrorApply.EndSnapshot(scope, machine, 0, null, 1000);
            Cards.Mirrored(scope, machine);
            return 0;
        });

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync(); await Cards.DisposeAsync();
            await Registry.DisposeAsync(); await _work.DisposeAsync(); Hub.Dispose(); Engine.Dispose();
        }
    }
}
