using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Messages;
using Assembly.Engine.Effects;
using Assembly.Engine;
using System.Text.Json;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine;

public sealed class SessionCutoverTests
{
    private const string Options = """[{"id":"model","category":"model","type":"select","currentValue":"wire","options":[{"value":"wire","name":"Friendly"},{"value":"chosen","name":"Chosen"}]}]""";

    [Fact]
    public async Task Startup_imports_only_local_owning_rows_preserving_actual_metadata_and_existing_shells()
    {
        using var f = new EngineFixture(); var topics = new Topics(); var launcher = new Launcher();
        var store = SessionStore.Open(f.Db, f.Time, "/checkouts");
        var root = store.Create("work-profile", "https://example.com/repo");
        store.SetAcpSession(root.SessionId, "acp-history");
        store.SetConfigOptions(root.SessionId, Options);
        store.SetConfigChoice(root.SessionId, "model", "chosen");
        var helper = store.Helper(root, "retired-launcher", "Helper");
        var native = store.Child(root, "native-acp", "Native");
        store.Add("foreign", "work-profile", "repo", "peer");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits,
            cutover: new(f.Db, store, launcher, "/checkouts", f.Time, _ => "/account-home"));
        Assert.Equal(2, f.Count("engine_session"));
        Assert.Equal("chosen", f.Text("SELECT model FROM engine_session WHERE id=$id", new SqlArg("$id", root.SessionId)));
        Assert.Equal("/account-home", f.Text("SELECT account_context FROM engine_session WHERE id=$id", new SqlArg("$id", root.SessionId)));
        Assert.Equal("retired-launcher", f.Text("SELECT provider FROM engine_session WHERE id=$id", new SqlArg("$id", helper.SessionId)));
        Assert.Null(f.Text("SELECT model FROM engine_session WHERE id=$id", new SqlArg("$id", helper.SessionId)));
        Assert.Equal("", f.Text("SELECT account_context FROM engine_session WHERE id=$id", new SqlArg("$id", helper.SessionId)));
        Assert.Equal("/checkouts/" + root.SessionId, store.Root(root));
        Assert.Equal(store.Root(root), store.Root(native));
        Assert.Null(store.Root(store.Get("foreign")!));
        Assert.Equal("acp-history", store.Get(root.SessionId)!.AcpSessionId);
        Assert.Null(store.Get(root.SessionId)!.Title);
        Assert.Null(store.Config(root.SessionId).PendingModel);
        Assert.Equal("https://example.com/repo", store.Get(helper.SessionId)!.RepoUrl);
        Assert.Equal(0, f.Count("engine_outbox")); Assert.Equal(0, f.Count("engine_message"));
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public async Task Bound_owner_create_config_and_title_use_one_checked_transaction_and_unchanged_facts_are_no_ops()
    {
        using var f = new EngineFixture(); var topics = new Topics(); var launcher = new Launcher();
        var store = SessionStore.Open(f.Db, f.Time, "/checkouts");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, store, launcher, "/checkouts", f.Time, _ => null));
        var created = store.Create("work-profile", "https://example.com/repo");
        Assert.Null(created.Title); Assert.Null(store.Get(created.SessionId)!.Title);
        Assert.Equal("", f.Text("SELECT title FROM engine_session WHERE id=$id", new SqlArg("$id", created.SessionId)));
        Assert.Equal(1, f.Count("engine_session")); Assert.Equal(0, f.Count("engine_message")); Assert.Equal(0, launcher.Calls);
        store.SetConfigOptions(created.SessionId, Options);
        var events = f.Events().Count; var receipts = f.Count("engine_command_receipt");
        store.SetConfigOptions(created.SessionId, Options);
        Assert.Equal(events, f.Events().Count); Assert.Equal(receipts, f.Count("engine_command_receipt"));
        store.SetTitle(created.SessionId, "Exact title", "helper");
        events = f.Events().Count; receipts = f.Count("engine_command_receipt");
        store.SetTitle(created.SessionId, "Losing title", "prompt");
        store.SetTitle(created.SessionId, "Exact title", "user");
        Assert.Equal("Exact title", f.Text("SELECT title FROM engine_session WHERE id=$id", new SqlArg("$id", created.SessionId)));
        Assert.Equal("user", store.Get(created.SessionId)!.TitleSource);
        Assert.Equal(events, f.Events().Count); Assert.Equal(receipts, f.Count("engine_command_receipt"));
    }

    [Fact]
    public void Import_failure_rolls_back_the_same_v3_schema_write_and_never_binds_the_store()
    {
        using var file = new TempDatabase(); var topics = new Topics(); var time = new FakeTimeProvider();
        using var db = AppDatabase.Open(file.Path, time, topics); RowStore.Open(db);
        db.Writer.Write("test.accepted-v3", s => s.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", "old-v3.sql"))));
        var store = SessionStore.Open(db, time, "/checkouts");
        store.Helper(new SessionInfo("missing-peer", "work-profile", "repo", null, null, 1), "work-profile", "Helper");
        var publications = 0;
        using var subscription = topics.Subscribe("__engine_committed", null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        Assert.Throws<InvalidOperationException>(() => EngineRuntime.Start(db, topics, time, new(() => 1, () => 5), cutover: new(db, store, new Launcher(), "/checkouts", time)));
        Assert.Equal(3L, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        Assert.Equal(0L, db.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('session_config') WHERE name='pending_model'", r => r.Long(0), default));
        Assert.Equal(0, publications);
        Assert.Null(store.Config(store.Create("work-profile", "repo").SessionId).PendingModel);
    }

    [Fact]
    public async Task Restart_cancels_only_provider_needs_resets_dead_running_and_preserves_starting_and_uncertain_messages()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        f.SeedSession("running", state: "running"); f.SeedSession("uncertain");
        f.Apply(new StartSession("work-profile", null, "/folder", "Starting", null, null, null), "starting");
        var system = new EngineActor(ActorKind.System, "provider");
        Assert.True(f.Apply(new OpenNeed("running", null, null, "permission", "Provider permission", ["allow", "deny"], "request", null, null), "provider-need", actor: system).Applied);
        Assert.True(f.Apply(new OpenNeed("running", null, null, "question", "Owner question", [], null, null, null), "owner-need", actor: system).Applied);
        Assert.True(f.Apply(new SendMessage(null, "uncertain", "Exact message", "prompt"), "message").Applied);
        var sendId = f.Text("SELECT id FROM engine_outbox WHERE kind='send'")!;
        Assert.True(f.Apply(new ClaimEffect(), sendId, actor: system).Applied);
        var messageBefore = f.Text("SELECT json_object('version',version,'state',state,'in_flight',in_flight,'attempt',attempt,'text',text) FROM engine_message WHERE id='message'");
        var store = SessionStore.Open(f.Db, f.Time, "/checkouts");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, store, new Launcher(), "/checkouts", f.Time));
        Assert.Equal("cancelled", f.State("engine_needs_me", "provider-need"));
        Assert.Equal("open", f.State("engine_needs_me", "owner-need"));
        Assert.Equal("needs_me", f.State("engine_session", "running"));
        Assert.Equal("idle", f.Text("SELECT activity_state FROM engine_session WHERE id='running'"));
        Assert.Equal("starting", f.State("engine_session", "starting"));
        Assert.Equal(messageBefore, f.Text("SELECT json_object('version',version,'state',state,'in_flight',in_flight,'attempt',attempt,'text',text) FROM engine_message WHERE id='message'"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='send'"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
        Assert.Contains(f.Events(), e => e.EntityId == "running" && e.Kind == "session.changed" && e.Data.Reason == "backend_restart");
        Assert.Contains(f.Events(), e => e.EntityId == "provider-need" && e.Data.Reason == "provider_gone");
    }

    [Fact]
    public async Task Worker_sees_repaired_shells_and_pending_intent_before_the_first_effect()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        Assert.True(f.Apply(new StartSession("work-profile", "wire", "/direct-root", "Root", null, null, null), "root").Applied);
        var store = SessionStore.Open(f.Db, f.Time, "/checkouts");
        var probe = new Probe(effect =>
        {
            Assert.Equal("root", effect.SessionId);
            Assert.Equal("/direct-root", store.Get("root")!.RepoUrl);
            Assert.Equal("/direct-root", store.Root(store.Get("root")!));
            Assert.Equal("wire", store.Config("root").PendingModel);
            return true;
        });
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, effects: probe,
            cutover: new(f.Db, store, new Launcher(), "/checkouts", f.Time));
        Assert.True(await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM session_info WHERE session_id='root'"));
        Assert.Equal(0, f.Count("engine_message"));
    }

    [Fact]
    public async Task Title_observer_failure_rolls_back_chat_priority_engine_receipt_event_and_publication()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        var store = SessionStore.Open(f.Db, f.Time, "/checkouts");
        var session = store.Create("work-profile", "repo");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, store, new Launcher(), "/checkouts", f.Time));
        store.SetTitle(session.SessionId, "Before", "helper");
        f.Db.Writer.Write("test.title-failure", scope => scope.Execute("CREATE TRIGGER refuse_title BEFORE UPDATE OF title ON engine_session WHEN NEW.title='After' BEGIN SELECT RAISE(ABORT,'title failed'); END"));
        var events = f.Events().Count; var receipts = f.Count("engine_command_receipt"); var publications = 0;
        using var subscription = topics.Subscribe(SessionsFeature.Topic, null, _ => { }, (_, _) => Interlocked.Increment(ref publications));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.SetTitle(session.SessionId, "After", "user"));
        Assert.Equal("Before", store.Get(session.SessionId)!.Title); Assert.Equal("helper", store.Get(session.SessionId)!.TitleSource);
        Assert.Equal("Before", f.Text("SELECT title FROM engine_session WHERE id=$id", new SqlArg("$id", session.SessionId)));
        Assert.Equal(events, f.Events().Count); Assert.Equal(receipts, f.Count("engine_command_receipt")); Assert.Equal(0, publications);
    }

    [Fact]
    public async Task New_bound_helpers_use_the_current_single_limits_policy_and_refusals_leave_no_chat_shell()
    {
        var topics = new Topics(); var depth = 1; var children = 1;
        using var f = new EngineFixture(new(() => depth, () => children), topics);
        var store = SessionStore.Open(f.Db, f.Time, "/checkouts");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, store, new Launcher(), "/checkouts", f.Time));
        var root = store.Create("work-profile", "repo"); var a = store.Helper(root, "work-profile", "A", root.SessionId);
        var before = f.Count("engine_session"); var chat = store.List(null, 20).Sessions.Count; var events = f.Events().Count;
        Assert.Contains("child_limit", Assert.Throws<Assembly.Application.Common.Calls.CallFailed>(() => store.Helper(root, "work-profile", "B", root.SessionId)).Message);
        Assert.Contains("depth_limit", Assert.Throws<Assembly.Application.Common.Calls.CallFailed>(() => store.Helper(a, "work-profile", "Deep", a.SessionId)).Message);
        Assert.Equal(before, f.Count("engine_session")); Assert.Equal(chat, store.List(null, 20).Sessions.Count); Assert.Equal(events, f.Events().Count);
        depth = 2; children = 2;
        var deep = store.Helper(a, "work-profile", "Deep", a.SessionId);
        Assert.Equal(a.SessionId, f.Text("SELECT parent_id FROM engine_session WHERE id=$id", new SqlArg("$id", deep.SessionId)));
    }

    [Fact]
    public async Task Confirmed_config_engine_failure_rolls_back_options_choice_and_pending_intent()
    {
        var topics = new Topics(); using var f = new EngineFixture(topics: topics);
        var store = SessionStore.Open(f.Db, f.Time, "/checkouts"); var session = store.Create("work-profile", "repo");
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, f.Limits, cutover: new(f.Db, store, new Launcher(), "/checkouts", f.Time));
        f.Db.Writer.Write("test.pending-failure", scope => scope.Execute("""
            INSERT INTO session_config(session_id,pending_model) VALUES($id,'original');
            CREATE TRIGGER refuse_model BEFORE UPDATE OF model ON engine_session WHEN NEW.model='wire' BEGIN SELECT RAISE(ABORT,'model failed'); END;
            """, new SqlArg("$id", session.SessionId)));
        var events = f.Events().Count; var receipts = f.Count("engine_command_receipt");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.ConfirmModelSelection(session.SessionId, "model", "wire", Options));
        Assert.Null(store.Config(session.SessionId).Options); Assert.Empty(store.Config(session.SessionId).Choices);
        Assert.Equal("original", store.Config(session.SessionId).PendingModel);
        Assert.Null(f.Text("SELECT model FROM engine_session WHERE id=$id", new SqlArg("$id", session.SessionId)));
        Assert.Equal(events, f.Events().Count); Assert.Equal(receipts, f.Count("engine_command_receipt"));
    }

    private sealed class Probe(Func<Effect, bool> inspect) : IEngineEffects
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken cancellationToken)
        {
            try { Reached.TrySetResult(inspect(effect)); } catch (Exception error) { Reached.TrySetException(error); }
            return Task.FromResult(new EffectReceipt(EffectDisposition.Unknown, JsonSerializer.SerializeToElement((string?)null, EngineJson.Default.String), "unknown"));
        }
        public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken cancellationToken) => ExecuteAsync(effect, cancellationToken);
    }

    private sealed class Launcher : IAgentLauncher
    {
        public int Calls;
        public IReadOnlyList<AgentDescriptor> Agents { get; } = [new("work-profile", "Friendly custom", "unused", [], null, VendorFamily: "codex")];
        public Task<string> CheckoutAsync(SessionInfo session, StageTimes stages, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("No checkout during projection"); }
        public void ValidateFolder(string folder) { Calls++; new Assembly.Infrastructure.Agents.AdapterLauncher([], "", new UnclaimedPermissions()).ValidateFolder(folder); }
        public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("No provider during projection"); }
    }
}
