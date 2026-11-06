using Assembly.Application.Common.Acp;
using Assembly.Application.Common;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Projects;
using Assembly.Infrastructure.Engine;
using Assembly.Engine.Sessions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions;

[Collection("Agent catalogue environment")]
public sealed class SessionInitialConfigTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly AppDatabase _db;
    private readonly SessionStore _store;
    private readonly FakeTimeProvider _time = new();
    private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("assembly-initial-config-");
    private EngineRuntime? _engine;
    private readonly FakeLauncher _launcher = new();
    public SessionInitialConfigTests()
    {
        _db = TestDatabase.Open(_file.Path);
        _store = SessionStore.Open(_db, _time, "/managed");
    }

    [Fact]
    public void Initial_raw_choices_target_and_folder_commit_together_and_survive_idempotent_open()
    {
        var choices = new Dictionary<string, string> { ["raw/模型\"selector"] = "model/β", ["effort"] = "medium" };
        var target = StartOwningEngine();
        var created = _db.Writer.Write("test.owner-create", scope => _store.Create(scope, "claude", "initial_raw",
            Path.Combine(_home.FullName, "assembly-bare"), null, null, initialConfig: new(target, choices)));
        var reopened = SessionStore.Open(_db, _time, "/managed");
        Assert.Equal(created, reopened.Get(created.SessionId));
        Assert.Equal(Path.Combine(_home.FullName, "assembly-bare"), reopened.Get(created.SessionId)!.WorkingFolder);
        Assert.Equal(new SessionLocation(Path.Combine(_home.FullName, "assembly-bare"), false, false), _store.Location(created));
        Assert.Equal(target, reopened.Config(created.SessionId).ConfigTarget);
        Assert.Equal(choices, reopened.Config(created.SessionId).Choices);
        Assert.Null(reopened.Config(created.SessionId).PendingModel);
        Assert.Null(created.AcpSessionId);
        Assert.Equal(1L, Count("pragma_table_info('session_config') WHERE name='pending_model'"));
        Assert.Equal(0L, Count("session_config WHERE pending_model IS NOT NULL"));
        Assert.Equal(1L, Count("pragma_table_info('session_config') WHERE name='config_target'"));
        Assert.Equal(1L, Count("pragma_table_info('session_info') WHERE name='working_folder'"));
    }

    [Fact]
    public void Config_insert_failure_rolls_back_the_session_and_all_choices()
    {
        var target = StartOwningEngine();
        _db.Writer.Write("test.reject", scope => scope.Execute("CREATE TRIGGER reject_initial BEFORE INSERT ON session_config BEGIN SELECT RAISE(ABORT,'fixture refusal'); END;"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _db.Writer.Write("test.owner-create", scope =>
            _store.Create(scope, "claude", "rollback_raw", Path.Combine(_home.FullName, "assembly-bare"), null, null,
                initialConfig: new(target, new Dictionary<string, string> { ["model"] = "wire" }))));
        Assert.Equal(0L, Count("session_info"));
        Assert.Equal(0L, Count("session_config"));
        Assert.Empty(_store.List(null, 10).Sessions);
        Assert.Equal(0L, Count("engine_session"));
        Assert.Equal(0L, Count("engine_outbox"));
    }

    [Fact]
    public void Bound_cutover_refuses_explicit_folder_before_any_create_or_observer_but_omission_is_unchanged()
    {
        var observer = new Observer(); _store.BindEngine(observer);
        var failure = Assert.Throws<CallFailed>(() => _store.Create("custom", "repo", new("target", new Dictionary<string, string> { ["model"] = "wire" }), "/direct"));
        Assert.Equal("incompatible_backend", failure.Reason);
        Assert.Equal("This backend cannot create a session in the selected folder.", failure.Message);
        Assert.Equal(0, observer.Starts);
        Assert.Equal(0L, Count("session_info"));
        Assert.Equal(0L, Count("session_config"));
        var ordinary = _store.Create("custom", "repo");
        Assert.Equal(1, observer.Starts);
        Assert.Equal(new SessionLocation("/managed/" + ordinary.SessionId, true), _store.Location(ordinary));
    }

    [Fact]
    public void Engine_location_wins_over_legacy_folder_while_children_share_the_owning_legacy_root()
    {
        var root = _store.Create("custom", "repo", workingFolder: "/legacy/direct");
        var child = _store.Child(root, "native-child", "Child");
        Assert.Equal(new SessionLocation("/legacy/direct", false), _store.Location(child));
        var observer = new Observer { FolderValue = "/engine/authoritative" }; _store.BindEngine(observer);
        Assert.Equal(new SessionLocation("/engine/authoritative", false), _store.Location(child));
        Assert.Equal("/managed/" + root.SessionId, _store.ManagedFolder(child));
    }

    [Fact]
    public void Final_creation_validation_refusal_rolls_back_without_saving_pending_intent()
    {
        Assert.Throws<CallFailed>(() => _store.Create("custom", "repo", new("target", new Dictionary<string, string> { ["model"] = "wire" }), validate: () => throw new CallFailed("fixture changed")));
        Assert.Equal(0L, Count("session_info"));
        Assert.Equal(0L, Count("session_config"));
    }

    [Fact]
    public void Create_keeps_the_captured_target_when_ambient_environment_changes_before_the_writer()
    {
        const string key = "ASSEMBLY_C3_CREATE_CAPTURE_FIXTURE";
        var prior = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "captured-fixture");
        try
        {
            var actual = new FakeLauncher();
            var target = ConfigTargetIdentity.Capture(actual, "claude").TargetId;
            var launcher = new MutatingLauncher(actual, () => Environment.SetEnvironmentVariable(key, "late-fixture"));
            StartOwningEngine(launcher);
            var created = _db.Writer.Write("test.captured-owner-create", scope => _store.Create(scope,
                "claude", "captured_target", Path.Combine(_home.FullName, "assembly-bare"), null, null,
                initialConfig: new(target, new Dictionary<string, string> { ["model"] = "wire" })));
            Assert.Equal("late-fixture", Environment.GetEnvironmentVariable(key));
            Assert.Equal(target, _store.Config(created.SessionId).ConfigTarget);
            Assert.NotEqual(target, ConfigTargetIdentity.Capture(actual, "claude").TargetId);
            Assert.Equal(0, actual.Launches);
        }
        finally { Environment.SetEnvironmentVariable(key, prior); }
    }

    private sealed class MutatingLauncher(IAgentLauncher inner, Action mutate) : IAgentLauncher
    {
        public IReadOnlyList<AgentDescriptor> Agents => inner.Agents;
        public bool CanRunTerminalAuthentication => inner.CanRunTerminalAuthentication;
        public void ValidateFolder(string folder) { inner.ValidateFolder(folder); mutate(); }
        public Task<IAgentLink> LaunchAsync(string agent, StageTimes stages, CancellationToken ct) => inner.LaunchAsync(agent, stages, ct);
        public Task<IAgentLink> LaunchConfiguredAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment, StageTimes stages, CancellationToken ct) =>
            inner.LaunchConfiguredAsync(descriptor, environment, stages, ct);
        public Task<IAgentLink> LaunchSignInAsync(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment,
            Assembly.Application.Common.Acp.IPermissionPolicy policy, StageTimes stages, CancellationToken ct) =>
            inner.LaunchSignInAsync(descriptor, environment, policy, stages, ct);
    }

    [Fact]
    public void A_config_choice_keeps_quoted_raw_provider_ids_and_existing_choices()
    {
        var session = _store.Create("custom", "repo");
        _store.SetConfigChoice(session.SessionId, "effort", "high");
        const string id = "model/模型\"selector";
        _store.SetConfigChoice(session.SessionId, id, "provider/β");
        var saved = _store.Config(session.SessionId).Choices;
        Assert.Equal("provider/β", saved[id]);
        Assert.Equal("high", saved["effort"]);
        Assert.Equal(2, saved.Count);
    }

    private long Count(string table) => _db.QueryFirst("SELECT COUNT(*) FROM " + table, row => row.Long(0), CancellationToken.None);
    private sealed class Observer : IEngineSessions
    {
        public int Starts; public string? FolderValue;
        public void Started(IWriteScope scope, SessionInfo session, string? initiatingSessionId, string? projectId = null,
            CheckoutIntent? checkout = null, SessionCreationPreparation? preparation = null) => Starts++;
        public void Titled(IWriteScope scope, string sessionId, string title, ReadOnlyMemory<byte> utf8) { }
        public void Configured(IWriteScope scope, string sessionId, string? model) { }
        public SessionLocation? Location(string sessionId) => FolderValue is { } folder ? new(folder, false) : null;
        public SessionLocation? Location(IReadScope scope, string sessionId) => Location(sessionId);
        public string? Folder(string sessionId) => FolderValue;
        public string? Folder(IReadScope scope, string sessionId) => FolderValue;
    }
    private string StartOwningEngine(IAgentLauncher? launcher = null)
    {
        var actual = launcher ?? _launcher;
        var cutover = new SessionCutover(_db, _store, actual, "/managed", _time, owningHome: _home.FullName);
        _engine = EngineRuntime.Start(_db, new TopicSet(), _time, new SessionLimits(() => 5, () => 10), cutover: cutover);
        return ConfigTargetIdentity.Capture(actual, "claude").TargetId;
    }
    public void Dispose()
    {
        _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _db.Dispose(); _file.Dispose(); _home.Delete(true);
    }
}
