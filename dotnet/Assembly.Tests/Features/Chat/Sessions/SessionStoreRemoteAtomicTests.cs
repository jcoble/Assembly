using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;
using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class SessionStoreRemoteAtomicTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1000));
    private readonly RecordingTopics _topics = new();
    private readonly AppDatabase _real;
    private readonly FaultDatabase _db;
    private readonly SessionSummaries _summaries;
    private readonly SessionStore _store;
    private const string Copy = "workbox-test";

    public SessionStoreRemoteAtomicTests()
    {
        _real = AppDatabase.Open(_file.Path, _time, _topics);
        _db = new(_real);
        _summaries = SessionSummaries.Load(RowStore.Open(_db));
        _store = SessionStore.Open(_db, _time, "checkouts", _summaries);
        _store.Add("copied", "claude", "repo", Copy);
        _store.SetTitle("copied", "old", "prompt");
        _topics.Sent.Clear();
    }

    [Fact]
    public void Remote_bare_classification_survives_projection_and_explicit_project_clears_it()
    {
        Assert.True(_store.ApplyRemote(Incoming() with { IsBare = true }, Copy));
        Assert.True(_store.Item(_store.Get("copied")!).IsBare);
        Assert.True(_store.ApplyRemote(Incoming() with { IsBare = true, ProjectId = "explicit" }, Copy));
        Assert.False(_store.Item(_store.Get("copied")!).IsBare);
    }

    [Fact]
    public void Guarded_wrong_or_missing_copy_has_no_metadata_summary_or_publication_side_effect()
    {
        var original = _store.Get("copied");
        Assert.False(_store.ApplyRemote(Incoming(), "another-machine"));
        Assert.False(_store.ApplyRemote(Incoming() with { SessionId = "unknown" }, Copy));
        Assert.Equal(original, _store.Get("copied"));
        Assert.Equal(SessionSummary.None, _summaries.Get("copied"));
        Assert.Equal(SessionSummary.None, _summaries.Get("unknown"));
        Assert.Empty(_topics.Sent);
        _real.Writer.Write("test.reassign", scope => scope.Execute("UPDATE session_info SET machine='new-owner' WHERE session_id='copied'"));
        Assert.False(_store.ApplyRemote(Incoming(), Copy));
        Assert.Equal("old", _store.Get("copied")!.Title);
        Assert.Empty(_topics.Sent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Committed_observer_sees_metadata_summary_and_serialized_item_together(bool guarded)
    {
        _db.BeforeCommit = () =>
        {
            Assert.Equal("old", _store.Get("copied")!.Title);
            Assert.Equal(SessionSummary.None, _summaries.Get("copied"));
            Assert.Empty(_topics.Sent);
        };
        _topics.OnPublish = (topic, json) =>
        {
            Assert.Equal("new\0😀", _store.Get("copied")!.Title);
            Assert.Equal(new SessionSummary("run", null, 0, true, 3), _summaries.Get("copied"));
            if (topic != "sessions") return;
            var item = JsonSerializer.Deserialize(json, SessionsJson.Default.SessionItem)!;
            Assert.Equal(_store.Item(_store.Get("copied")!), item);
            Assert.Equal(Copy, item.Machine); // Incoming provenance is not the copied-row guard.
        };
        if (guarded) Assert.True(_store.ApplyRemote(Incoming(), Copy));
        else _store.ApplyRemote(Incoming());
        Assert.Equal(2, _topics.Sent.Count);
    }

    [Theory]
    [InlineData("user", false, "helper", "new", "old", "user")]
    [InlineData("prompt", true, "user", "new", "old", "prompt")]
    [InlineData("helper", false, "prompt", "new", "old", "helper")]
    [InlineData("prompt", false, "user", null, "old", "prompt")]
    [InlineData(null, false, null, "", "", "prompt")]
    [InlineData("prompt", false, "helper", "new", "new", "helper")]
    public void Guarded_and_public_paths_retain_title_gates_and_still_update_summary(string? currentSource,
        bool helper, string? source, string? title, string expectedTitle, string expectedSource)
    {
        _real.Writer.Write("test.gates", scope => scope.Execute("UPDATE session_info SET title_source=$source,helper=$helper WHERE session_id='copied'",
            new SqlArg("$source", currentSource), new SqlArg("$helper", helper ? 1 : 0)));
        var item = Incoming() with { Title = title, TitleSource = source };
        Assert.True(_store.ApplyRemote(item, Copy));
        _store.ApplyRemote(item);
        var actual = _store.Get("copied")!;
        Assert.Equal(expectedTitle, actual.Title);
        Assert.Equal(expectedSource, actual.TitleSource);
        Assert.Equal(2000, actual.UpdatedAt);
        Assert.Equal(new SessionSummary("run", null, 0, true, 3), _summaries.Get("copied"));
    }

    [Fact]
    public void Original_unknown_row_still_updates_summary_without_a_publication()
    {
        _store.ApplyRemote(Incoming() with { SessionId = "unknown" });
        Assert.Null(_store.Get("unknown"));
        Assert.Equal(new SessionSummary("run", null, 0, true, 3), _summaries.Get("unknown"));
        Assert.Empty(_topics.Sent);
    }

    [Fact]
    public void Missing_summary_service_keeps_the_original_none_projection()
    {
        var store = SessionStore.Open(_real, _time, "checkouts");
        Assert.True(store.ApplyRemote(Incoming(), Copy));
        var item = JsonSerializer.Deserialize(_topics.Sent.Single(x => x.Topic == "sessions").Json, SessionsJson.Default.SessionItem)!;
        Assert.Equal("idle", item.State);
        Assert.Null(item.ActiveRunId);
        Assert.False(item.PendingAsk);
        Assert.Equal(0, item.Background);
    }

    [Fact]
    public void Deferred_fk_commit_failure_rolls_back_metadata_summary_and_queued_publications()
    {
        _real.Writer.Write("test.deferred", scope => scope.Execute("""
            CREATE TABLE parent_fault(id INTEGER PRIMARY KEY);
            CREATE TABLE child_fault(id INTEGER REFERENCES parent_fault(id) DEFERRABLE INITIALLY DEFERRED);
            CREATE TRIGGER fail_remote AFTER UPDATE OF updated_at ON session_info BEGIN INSERT INTO child_fault VALUES(42); END;
            """));
        _db.BeforeCommit = () =>
        {
            Assert.Equal(SessionSummary.None, _summaries.Get("copied"));
            Assert.Empty(_topics.Sent);
        };
        Assert.Throws<SqliteException>(() => _store.ApplyRemote(Incoming(), Copy));
        AssertRolledBack();
        Assert.Equal(0, _real.QueryFirst("SELECT COUNT(*) FROM child_fault", row => row.Long(0), default));
    }

    [Fact]
    public void Actual_json_serialization_failure_leaves_no_uncommitted_summary_visibility_or_write()
    {
        _db.SerializationFault = () =>
        {
            Assert.Equal(SessionSummary.None, _summaries.Get("copied"));
            Assert.Equal("old", _store.Get("copied")!.Title);
            Assert.Empty(_topics.Sent);
        };
        Assert.Throws<JsonException>(() => _store.ApplyRemote(Incoming(), Copy));
        AssertRolledBack();
    }

    [Fact]
    public void Aftercommit_merge_preserves_a_local_endseq_advance_after_publication_capture()
    {
        _db.BeforeCommit = () => _summaries.Apply("copied", [new StoredRow("copied", 7, "local", null,
            RowKind.Turn, JsonDocument.Parse("{\"state\":\"finished\"}").RootElement, 1000)]);
        Assert.True(_store.ApplyRemote(Incoming(), Copy));
        Assert.Equal(7, _summaries.Get("copied").EndSeq);
        Assert.Equal("run", _summaries.Get("copied").RunId);
        // Publication captures the summary before commit; preserving later EndSeq is the existing race boundary.
        var published = JsonSerializer.Deserialize(_topics.Sent.Single(x => x.Topic == "sessions").Json, SessionsJson.Default.SessionItem)!;
        Assert.False(published.Unread);
        Assert.True(_store.Item(_store.Get("copied")!).Unread);
    }

    [Fact]
    public void Callback_failure_is_reported_after_commit_and_before_topics_without_claiming_rollback()
    {
        Assert.Throws<IOException>(() => _real.Writer.Write("test.callback", scope =>
        {
            scope.Execute("UPDATE session_info SET title='committed' WHERE session_id='copied'");
            scope.AfterCommit(() => throw new IOException("postcommit"));
            scope.Publish("probe", null, "queued", SessionsJson.Default.String);
            return 0;
        }));
        Assert.Equal("committed", _store.Get("copied")!.Title);
        Assert.Empty(_topics.Sent);
    }

    private void AssertRolledBack()
    {
        Assert.Equal("old", _store.Get("copied")!.Title);
        Assert.Equal(1000, _store.Get("copied")!.UpdatedAt);
        Assert.Equal(SessionSummary.None, _summaries.Get("copied"));
        Assert.Empty(_topics.Sent);
    }

    private SessionItem Incoming() => _store.Item(_store.Get("copied")!) with
    {
        Title = "new\0😀",
        TitleSource = "helper",
        UpdatedAt = 2000,
        Machine = "publisher-provenance",
        State = "running",
        ActiveRunId = "run",
        PendingAsk = true,
        Background = 3
    };

    private sealed class RecordingTopics : ITopics
    {
        public List<(string Topic, string Json)> Sent { get; } = [];
        public Action<string, string>? OnPublish;
        public event Action<string, string?>? Unsubscribed { add { } remove { } }
        public bool HasSubscribers(string topic, string key) => true;
        public void Publish(string topic, string? key, string json, bool keyedOnly = false)
        {
            OnPublish?.Invoke(topic, json);
            Sent.Add((topic, json));
        }
    }

    private sealed class FaultDatabase(AppDatabase inner) : IAppDatabase, IAtomicWriter
    {
        public Action? BeforeCommit;
        public Action? SerializationFault;
        public IAtomicWriter Writer => this;
        public IReadSnapshot OpenSnapshot(CancellationToken cancel) => inner.OpenSnapshot(cancel);
        public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => inner.Query(sql, map, cancel, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => inner.QueryFirst(sql, map, cancel, args);
        public T Write<T>(string what, Func<IWriteScope, T> write) => inner.Writer.Write(what, scope =>
        {
            var remote = what == "sessions.remote";
            var result = write(remote && SerializationFault is { } fault ? new SerializationScope(scope, fault) : scope);
            if (remote) BeforeCommit?.Invoke();
            return result;
        });
    }

    private sealed class SerializationScope(IWriteScope inner, Action fault) : IWriteScope
    {
        public void AfterCommit(Action publishState) => inner.AfterCommit(publishState);
        public int Execute(string sql, params SqlArg[] args) => inner.Execute(sql, args);
        public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => inner.Query(sql, map, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args) => inner.QueryFirst(sql, map, args);
        public void Publish<T>(string topic, string? key, T change, JsonTypeInfo<T> type, bool keyedOnly = false)
        {
            if (topic == "sessions")
            {
                var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
                options.Converters.Add(new FaultConverter(fault));
                type = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
            }
            inner.Publish(topic, key, change, type, keyedOnly);
        }
    }

    private sealed class FaultConverter(Action fault) : JsonConverter<SessionItem>
    {
        public override SessionItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, SessionItem value, JsonSerializerOptions options)
        {
            fault();
            throw new JsonException("injected actual serialization failure");
        }
    }

    public void Dispose() { _real.Dispose(); _file.Dispose(); }
}
