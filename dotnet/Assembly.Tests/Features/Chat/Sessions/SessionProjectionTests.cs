using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class SessionProjectionTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly AppDatabase _db;
    private readonly SessionStore _store;
    private const string Options = """[{"id":"model","category":"model","type":"select","currentValue":"wire","options":[{"value":"wire","name":"Friendly"}]}]""";
    public SessionProjectionTests()
    {
        _db = TestDatabase.Open(_file.Path);
        _store = SessionStore.Open(_db, new FakeTimeProvider(), "/checkouts");
    }

    [Fact]
    public void Legacy_unbound_config_stays_readable_and_known_schema_reads_uncommitted_intent()
    {
        var session = _store.Create("custom", "repo");
        _store.SetConfigOptions(session.SessionId, Options);
        _store.SetConfigChoice(session.SessionId, "model", "wire");
        Assert.Null(_store.Config(session.SessionId).PendingModel);
        Assert.Equal(0L, PendingColumns());
        Assert.Throws<IOException>(() => _db.Writer.Write("test.rollback", s =>
        {
            SessionStore.EnsureEngineProjectionSchema(s);
            s.Execute("UPDATE session_config SET pending_model='pending'");
            Assert.Equal("pending", _store.ConfigForEngineInitialization(s, session.SessionId).PendingModel);
            throw new IOException();
#pragma warning disable CS0162
            return 0;
#pragma warning restore CS0162
        }));
        Assert.Equal(0L, PendingColumns());
        Assert.Equal("wire", _store.Config(session.SessionId).Choices["model"]);
    }

    [Fact]
    public void Confirmed_selection_options_intent_and_observation_commit_or_roll_back_together()
    {
        var session = _store.Create("custom", "repo");
        _db.Writer.Write("test.schema", s => { SessionStore.EnsureEngineProjectionSchema(s); return 0; });
        _db.Writer.Write("test.intent", s => s.Execute("INSERT INTO session_config(session_id,pending_model) VALUES ($id,'pending')", new SqlArg("$id", session.SessionId)));
        var observer = new Observer();
        _store.BindEngine(observer);
        observer.Fail = true;
        Assert.Throws<IOException>(() => _db.Writer.Write("test.confirm", s => { _store.ConfirmModelSelection(s, session.SessionId, "model", "wire", Options); return 0; }));
        Assert.Equal("pending", _store.Config(session.SessionId).PendingModel);
        Assert.Null(_store.Config(session.SessionId).Options);
        Assert.Empty(_store.Config(session.SessionId).Choices);
        observer.Fail = false;
        _db.Writer.Write("test.confirm", s => { _store.ConfirmModelSelection(s, session.SessionId, "model", "wire", Options); return 0; });
        Assert.Null(_store.Config(session.SessionId).PendingModel);
        Assert.Equal(Options, _store.Config(session.SessionId).Options);
        Assert.Equal("wire", _store.Config(session.SessionId).Choices["model"]);
        Assert.Equal("wire", observer.Model);
        Assert.Throws<InvalidOperationException>(() => _store.BindEngine(new Observer()));
    }

    [Fact]
    public void Ordinary_config_observation_uses_raw_value_and_keeps_pending_until_confirmed()
    {
        var session = _store.Create("custom", "repo");
        _db.Writer.Write("test.schema", s => { SessionStore.EnsureEngineProjectionSchema(s); return 0; });
        _db.Writer.Write("test.intent", s => s.Execute("INSERT INTO session_config(session_id,pending_model) VALUES ($id,'pending')", new SqlArg("$id", session.SessionId)));
        var observer = new Observer(); _store.BindEngine(observer);
        _store.SetConfigOptions(session.SessionId, Options);
        Assert.Equal("wire", observer.Model);
        Assert.Equal("pending", _store.Config(session.SessionId).PendingModel);
        observer.Fail = true;
        Assert.Throws<IOException>(() => _store.SetConfigOptions(session.SessionId, "[]"));
        Assert.Equal(Options, _store.Config(session.SessionId).Options);
    }

    [Fact]
    public void Owning_creation_observes_the_insert_in_its_transaction_and_refusal_rolls_everything_back()
    {
        var observer = new Observer(); _store.BindEngine(observer);
        observer.FailStart = true;
        Assert.Throws<IOException>(() => _store.Create("custom", "repo"));
        Assert.Empty(_store.List(null, 20).Sessions);
        observer.FailStart = false;
        var root = _store.Create("custom", "repo");
        var helper = _store.Helper(root, "custom", "Helper", initiatingSessionId: root.SessionId);
        Assert.Equal(new[] { (root.SessionId, (string?)null), (helper.SessionId, root.SessionId) }, observer.Starts);
        _store.Child(root, "native", "Native");
        var peer = _store.Add("peer", "custom", "repo", "foreign");
        var foreignHelper = _store.Helper(peer, "custom", "Foreign helper");
        Assert.Equal("foreign", foreignHelper.Machine);
        Assert.Equal(2, observer.Starts.Count);
    }

    [Fact]
    public void Projection_and_intent_are_inserted_once_without_resetting_existing_chat_metadata()
    {
        var shell = new SessionInfo("root", "custom", "/direct", "Title", "acp-existing", 1000);
        _db.Writer.Write("test.project", scope =>
        {
            SessionStore.EnsureEngineProjectionSchema(scope);
            _store.ProjectEngineSession(scope, shell, "wire", "/direct");
            Assert.Equal(shell, _store.Get(scope, "root"));
            Assert.Equal("wire", _store.ConfigForEngineInitialization(scope, "root").PendingModel);
            _store.ProjectEngineSession(scope, shell with { Agent = "other", Title = "Overwrite", AcpSessionId = null }, "other", "/other");
            Assert.Equal(shell, _store.Get(scope, "root"));
            Assert.Equal("wire", _store.ConfigForEngineInitialization(scope, "root").PendingModel);
            return 0;
        });
        var observer = new Observer(); _store.BindEngine(observer);
        Assert.Equal("acp-existing", _store.Get("root")!.AcpSessionId);
        _db.Writer.Write("test.confirm", scope => { _store.ConfirmModelSelection(scope, "root", "model", "wire", Options); return 0; });
        _db.Writer.Write("test.reproject", scope => { _store.ProjectEngineSession(scope, shell, "wire", "/direct"); return 0; });
        Assert.Null(_store.Config("root").PendingModel);
    }

    [Fact]
    public void Deliberate_cached_model_choice_clears_intent_but_nonmodel_and_unconfirmed_choices_preserve_it()
    {
        var session = _store.Create("custom", "repo");
        _db.Writer.Write("test.schema", scope => { SessionStore.EnsureEngineProjectionSchema(scope); return 0; });
        _store.BindEngine(new Observer());
        _store.SetConfigOptions(session.SessionId, Options.Replace("\"id\":\"model\"", "\"id\":\"wire-selector\""));
        _db.Writer.Write("test.intent", scope => scope.Execute("UPDATE session_config SET pending_model='original' WHERE session_id=$id", new SqlArg("$id", session.SessionId)));
        _store.SetConfigChoice(session.SessionId, "model", "wire");
        Assert.Equal("original", _store.Config(session.SessionId).PendingModel);
        _store.SetUnconfirmedConfigChoice(session.SessionId, "wire-selector", "wire");
        Assert.Equal("original", _store.Config(session.SessionId).PendingModel);
        _store.SetConfigChoice(session.SessionId, "wire-selector", "wire");
        Assert.Null(_store.Config(session.SessionId).PendingModel);
        _db.Writer.Write("test.fail-clear", scope => scope.Execute("""
            UPDATE session_config SET pending_model='original';
            CREATE TRIGGER refuse_intent_clear BEFORE UPDATE OF pending_model ON session_config WHEN NEW.pending_model IS NULL BEGIN SELECT RAISE(ABORT,'keep intent'); END;
            """));
        var before = _store.Config(session.SessionId).Choices["wire-selector"];
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _store.SetConfigChoice(session.SessionId, "wire-selector", "changed"));
        Assert.Equal(before, _store.Config(session.SessionId).Choices["wire-selector"]);
        Assert.Equal("original", _store.Config(session.SessionId).PendingModel);
    }

    private long PendingColumns() => _db.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('session_config') WHERE name='pending_model'", r => r.Long(0), CancellationToken.None);
    private sealed class Observer : IEngineSessions
    {
        public bool Fail; public bool FailStart; public string? Model;
        public List<(string, string?)> Starts { get; } = [];
        public void Started(IWriteScope scope, SessionInfo session, string? initiatingSessionId)
        {
            Assert.Equal(session.Agent, scope.QueryFirst("SELECT agent FROM session_info WHERE session_id=$id", r => r.Text(0), new SqlArg("$id", session.SessionId)));
            if (FailStart) throw new IOException("start refused");
            Starts.Add((session.SessionId, initiatingSessionId));
        }
        public void Titled(IWriteScope scope, string sessionId, string actualStoredTitle, ReadOnlyMemory<byte> exactTitleUtf8) { }
        public void Configured(IWriteScope scope, string sessionId, string? model)
        {
            Assert.Equal(model, ConfigOptions.ModelValue(System.Text.Json.JsonDocument.Parse(scope.QueryFirst("SELECT options FROM session_config WHERE session_id=$id", r => r.Text(0), new SqlArg("$id", sessionId))!).RootElement));
            if (Fail) throw new IOException("observation failed");
            Model = model;
        }
        public SessionLocation? Location(string sessionId) => null;
        public SessionLocation? Location(IReadScope scope, string sessionId) => null;
        public string? Folder(string sessionId) => null;
        public string? Folder(IReadScope scope, string sessionId) => null;
    }
    public void Dispose() { _db.Dispose(); _file.Dispose(); }
}
