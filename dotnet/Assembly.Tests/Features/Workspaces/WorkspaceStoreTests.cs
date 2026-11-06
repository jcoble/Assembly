using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;
using Assembly.Application.Features.Workspaces;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Features.Workspaces;

public sealed class WorkspaceStoreTests
{
    private static readonly WorkspaceKey Key = new("actual-source", "s", "/checkout");
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    [Fact]
    public void Revision_conflict_preserves_the_previous_snapshot_and_publishes_only_committed_changes()
    {
        using var file = new TempDatabase(); var topics = new RecordedTopics();
        using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        var store = new WorkspaceStore(db, TimeProvider.System);
        Assert.Equal(0, store.Read(Key, default).Revision); Assert.Null(store.Read(Key, default).Snapshot);
        var snapshot = Json("{\"version\":1,\"files\":[{\"path\":\"a\",\"draft\":\"exact é\\r\\n\"}]}");
        topics.OnPublish = () =>
        {
            // A separate read connection must see the durable revision when the invalidation is published.
            Assert.Equal(1, store.Read(Key, default).Revision);
            Assert.Equal(snapshot.GetRawText(), store.Read(Key, default).Snapshot!.Value.GetRawText());
        };
        Assert.Equal(1, db.Writer.Write("save", scope => store.Save(scope, Key, 0, snapshot)).Revision);
        Assert.Throws<CallFailed>(() => db.Writer.Write("stale", scope => store.Save(scope, Key, 0, Json("{}"))));
        Assert.Equal(snapshot.GetRawText(), store.Read(Key, default).Snapshot!.Value.GetRawText());
        Assert.Single(topics.Changes);
        Assert.Equal("workspaces", topics.Changes[0].Topic);
        Assert.Equal("actual-source/s//checkout", topics.Changes[0].Key);
        Assert.True(topics.Changes[0].KeyedOnly);
    }

    [Fact]
    public void Reopening_keeps_unknown_pane_fields_and_versions_opaque_without_changing_engine_state()
    {
        using var file = new TempDatabase();
        var snapshot = Json("""{"version":999,"futurePane":{"nested":[null,"exact é\r\n",false]},"files":[]}""");
        using (var db = TestDatabase.Open(file.Path))
        {
            var store = new WorkspaceStore(db, TimeProvider.System);
            db.Writer.Write("save", scope => store.Save(scope, Key, 0, snapshot));
            Assert.Equal(999, db.QueryFirst("SELECT schema_version FROM workspace_snapshot", row => row.Long(0), default));
            Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'engine_%'", row => row.Long(0), default));
        }
        using var reopened = TestDatabase.Open(file.Path);
        var actual = new WorkspaceStore(reopened, TimeProvider.System).Read(Key, default);
        Assert.Equal(1, actual.Revision);
        Assert.Equal(snapshot.GetRawText(), actual.Snapshot!.Value.GetRawText());
    }

    [Fact]
    public void Tuple_keys_separate_two_machines_and_two_checkouts_of_the_same_session()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path); var store = new WorkspaceStore(db, TimeProvider.System);
        var otherRoot = Key with { CheckoutRoot = "/other" }; var otherMachine = Key with { Machine = "other-source" };
        foreach (var key in new[] { Key, otherRoot, otherMachine })
            db.Writer.Write("save", scope => store.Save(scope, key, 0, Json(JsonSerializer.Serialize(new { version = 1, activePath = key.Machine + key.CheckoutRoot }))));
        Assert.Equal("actual-source/checkout", store.Read(Key, default).Snapshot!.Value.GetProperty("activePath").GetString());
        Assert.Equal("actual-source/other", store.Read(otherRoot, default).Snapshot!.Value.GetProperty("activePath").GetString());
        Assert.Equal("other-source/checkout", store.Read(otherMachine, default).Snapshot!.Value.GetProperty("activePath").GetString());
    }

    [Fact]
    public void Exact_utf8_limit_is_accepted_and_one_byte_more_preserves_prior_state()
    {
        using var file = new TempDatabase(); var topics = new RecordedTopics(); using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        var store = new WorkspaceStore(db, TimeProvider.System);
        // Raw Unicode is counted in bytes, including the JSON quotes; not UTF-16 character count.
        var exact = Json("\"" + new string('é', (WorkspaceStore.MaxSnapshotBytes - 2) / 2) + "\"");
        db.Writer.Write("exact", scope => store.Save(scope, Key, 0, exact));
        var over = Json("\"" + new string('é', (WorkspaceStore.MaxSnapshotBytes - 2) / 2) + "x\"");
        Assert.Throws<CallFailed>(() => db.Writer.Write("over", scope => store.Save(scope, Key, 1, over)));
        Assert.Equal(1, store.Read(Key, default).Revision);
        Assert.Equal(exact.GetRawText(), store.Read(Key, default).Snapshot!.Value.GetRawText());
        Assert.Single(topics.Changes);
    }

    [Fact]
    public void Later_failure_rolls_back_both_the_workspace_and_its_publication()
    {
        using var file = new TempDatabase(); var topics = new RecordedTopics(); using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics);
        var store = new WorkspaceStore(db, TimeProvider.System);
        Assert.Throws<InvalidOperationException>(() => db.Writer.Write<int>("rollback", scope =>
        {
            store.Save(scope, Key, 0, Json("{\"version\":1}"));
            store.Save(scope, Key with { CheckoutRoot = "/other" }, 0, Json("{\"version\":1}"));
            throw new InvalidOperationException("later write failed");
        }));
        Assert.Equal(0, store.Read(Key, default).Revision); Assert.Null(store.Read(Key, default).Snapshot);
        Assert.Equal(0, store.Read(Key with { CheckoutRoot = "/other" }, default).Revision);
        Assert.Empty(topics.Changes);
    }

    private sealed class RecordedTopics : ITopics
    {
        public readonly List<(string Topic, string? Key, string Json, bool KeyedOnly)> Changes = [];
        public Action? OnPublish { get; set; }
        public void Publish(string topic, string? key, string json, bool keyedOnly = false)
        {
            OnPublish?.Invoke();
            Changes.Add((topic, key, json, keyedOnly));
        }
        public bool HasSubscribers(string topic, string key) => true;
        public event Action<string, string?>? Unsubscribed { add { } remove { } }
    }
}
