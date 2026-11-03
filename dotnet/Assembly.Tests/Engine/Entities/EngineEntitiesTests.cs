using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;
using Assembly.Engine.NeedsMe;

namespace Assembly.Tests.Engine;

public sealed class EngineEntitiesTests
{
    [Fact]
    public void Two_hundred_same_kind_reads_are_batched_and_keep_the_entity_display_contract()
    {
        using var f = new EngineFixture();
        var refs = Enumerable.Range(0, 200).Select(i => new EngineEntityRef("session", $"s{i}")).ToArray();
        foreach (var item in refs) f.SeedSession(item.Id);
        var observed = new ObservedDatabase(f.Db);
        var page = new EngineReader(observed, f.Machine).Entities(refs, null, default);
        Assert.Equal(refs.Select(item => item.Id), page.Items.Select(item => item.GetProperty("id").GetString()));
        Assert.Null(page.Next);
        Assert.Equal(1, observed.Opened);
        Assert.Equal(0, observed.Active);
        Assert.Equal(3, observed.Queries); // Owner metadata plus one size batch and one payload batch.
        Assert.Equal(new EngineReader(f.Db, f.Machine).Entity("session", "s0", default)!.Value.GetRawText(), page.Items[0].GetRawText());
    }

    [Fact]
    public void Missing_and_duplicate_references_do_not_duplicate_items_or_stall_progress()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var page = new EngineReader(f.Db, f.Machine).Entities(
            [new("session", "missing"), new("session", "s"), new("session", "s"), new("session", "gone")], null, default);
        Assert.Equal("s", Assert.Single(page.Items).GetProperty("id").GetString());
        Assert.Null(page.Next);
    }

    [Fact]
    public void Metadata_and_payload_come_from_one_snapshot_even_when_a_commit_intervenes()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var observed = new ObservedDatabase(f.Db);
        observed.BeforeQuery = count =>
        {
            if (count == 2) f.Db.Writer.Write("test.concurrent", s => s.Execute("UPDATE engine_session SET title='Changed' WHERE id='s'"));
        };
        var page = new EngineReader(observed, f.Machine).Entities([new("session", "s")], null, default);
        Assert.Equal("Test", Assert.Single(page.Items).GetProperty("title").GetString());
        Assert.Equal("Changed", new EngineReader(f.Db, f.Machine).Entity("session", "s", default)!.Value.GetProperty("title").GetString());
        Assert.Equal(0, observed.Active);
    }

    [Fact]
    public void Large_display_items_page_without_loss_under_the_serialized_response_budget()
    {
        using var f = new EngineFixture();
        var refs = Mirror(f, 80);
        var reader = new EngineReader(f.Db, f.Machine);
        var ids = new List<string?>();
        string? cursor = null;
        do
        {
            var page = reader.Entities(refs, cursor, default, "remote", "bulk-one", FrameBytes);
            Assert.InRange(FrameBytes(page), 1, 1024 * 1024);
            Assert.NotEmpty(page.Items);
            ids.AddRange(page.Items.Select(item => item.GetProperty("id").GetString()));
            Assert.NotEqual(cursor, page.Next ?? "finished");
            cursor = page.Next;
        } while (cursor is not null);
        Assert.Equal(refs.Select(item => item.Id), ids);
    }

    [Fact]
    public void Continuations_cannot_change_refs_connection_machine_or_mirror_generation()
    {
        using var f = new EngineFixture(); var refs = Mirror(f, 40);
        var reader = new EngineReader(f.Db, f.Machine);
        var next = reader.Entities(refs, null, default, "remote", "bulk-one").Next;
        Assert.NotNull(next);
        Assert.Throws<ArgumentException>(() => reader.Entities(refs, next, default, "remote", "bulk-two"));
        Assert.Throws<ArgumentException>(() => reader.Entities(refs.Reverse().ToArray(), next, default, "remote", "bulk-one"));
        Assert.Throws<ArgumentException>(() => reader.Entities(refs, next, default, f.Machine, "bulk-one"));
        Assert.Throws<KeyNotFoundException>(() => reader.Entities(refs, next, default, "saved-machine-name", "bulk-one"));
        Mirror(f, 40);
        Assert.Throws<ArgumentException>(() => reader.Entities(refs, next, default, "remote", "bulk-one"));
    }

    [Fact]
    public void Oversize_and_private_kinds_refuse_and_always_dispose_the_snapshot()
    {
        using var f = new EngineFixture(); var refs = Mirror(f, 1, 40_000);
        var observed = new ObservedDatabase(f.Db);
        var reader = new EngineReader(observed, f.Machine);
        Assert.Throws<InvalidOperationException>(() => reader.Entities(refs, null, default, "remote"));
        Assert.Equal(0, observed.Active);
        Assert.Throws<ArgumentException>(() => reader.Entities([new("outbox", "private")], null, default));
        Assert.Throws<ArgumentException>(() => reader.Entities(Enumerable.Repeat(new EngineEntityRef("session", "s"), 201).ToArray(), null, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Entities([], null, cancellation.Token));
        Assert.Equal(0, observed.Active);
    }

    [Fact]
    public void Escaped_prefix_projection_keeps_existing_preview_semantics_and_checks_the_final_item()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.True(f.Apply(new OpenNeed("s", null, null, "permission", new string('<', 5_000), ["allow"], null, null, null),
            "need", actor: new(Assembly.Engine.Contracts.ActorKind.System, "adapter")).Applied);
        var reader = new EngineReader(f.Db, f.Machine);
        var item = Assert.Single(reader.Entities([new("needs_me", "need")], null, default).Items);
        Assert.Equal(reader.Entity("needs_me", "need", default)!.Value.GetRawText(), item.GetRawText());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(item, EngineJson.Default.JsonElement).Length <= 32 * 1024);
        // Unlike the bounded text prefix, options are part of the unchanged display. Escaping can exceed the
        // final item budget even when SQLite's raw JSON is smaller than 32 KiB; it must refuse rather than shorten it.
        f.Db.Writer.Write("test.escaped-options", scope => scope.Execute(
            "UPDATE engine_needs_me SET options_json=$options WHERE id='need'", new SqlArg("$options", "[\"" + new string('<', 6000) + "\"]")));
        Assert.Throws<InvalidOperationException>(() => reader.Entities([new("needs_me", "need")], null, default));
    }

    [Fact]
    public void Final_response_measurement_is_authoritative_even_when_the_payload_estimate_fits()
    {
        using var f = new EngineFixture(); f.SeedSession();
        var observed = new ObservedDatabase(f.Db);
        Assert.Throws<InvalidOperationException>(() => new EngineReader(observed, f.Machine).Entities(
            [new("session", "s")], null, default, measure: _ => 1024 * 1024 + 1));
        Assert.Equal(0, observed.Active);
    }

    private static EngineEntityRef[] Mirror(EngineFixture f, int count, int textBytes = 28_000)
    {
        var refs = Enumerable.Range(0, count).Select(i => new EngineEntityRef("session", $"r{i}")).ToArray();
        f.Db.Writer.Write("test.mirror", scope =>
        {
            MirrorApply.Open(scope, "remote", "saved-machine-name", 1000);
            MirrorApply.BeginSnapshot(scope, "remote", 1000);
            // These are display-only fixture rows, not operational session authority.
            var rows = refs.Select(item => JsonSerializer.Deserialize($$"""{"entityKind":"session","id":"{{item.Id}}","machine":"remote","version":1,"title":"{{new string('x', textBytes)}}"}""", EngineJson.Default.JsonElement)).ToArray();
            MirrorApply.SnapshotPage(scope, "remote", rows);
            MirrorApply.EndSnapshot(scope, "remote", 0, null, 1000);
            return 0;
        });
        return refs;
    }

    private static int FrameBytes(EngineEntitiesPage page)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("t", "result"); writer.WriteNumber("id", long.MaxValue);
            writer.WriteStartObject("result"); writer.WriteStartArray("items");
            foreach (var item in page.Items) item.WriteTo(writer);
            writer.WriteEndArray(); if (page.Next is not null) writer.WriteString("next", page.Next);
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        return checked((int)bytes.Length);
    }

    private sealed class ObservedDatabase(IAppDatabase inner) : IAppDatabase
    {
        public int Queries { get; private set; }
        public int Opened { get; private set; }
        public int Active { get; private set; }
        public Action<int>? BeforeQuery { get; set; }
        public IAtomicWriter Writer => inner.Writer;
        public IReadSnapshot OpenSnapshot(CancellationToken cancellationToken)
        { var snapshot = inner.OpenSnapshot(cancellationToken); Opened++; Active++; return new Snapshot(this, snapshot); }
        public List<T> Query<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => inner.Query(sql, map, cancel, args);
        public T? QueryFirst<T>(string sql, Func<IRow, T> map, CancellationToken cancel, params SqlArg[] args) => inner.QueryFirst(sql, map, cancel, args);
        private sealed class Snapshot(ObservedDatabase owner, IReadSnapshot inner) : IReadSnapshot
        {
            public List<T> Query<T>(string sql, Func<IRow, T> map, params SqlArg[] args)
            {
                owner.Queries++;
                owner.BeforeQuery?.Invoke(owner.Queries);
                return inner.Query(sql, map, args);
            }
            public T? QueryFirst<T>(string sql, Func<IRow, T> map, params SqlArg[] args)
            {
                owner.Queries++;
                owner.BeforeQuery?.Invoke(owner.Queries);
                return inner.QueryFirst(sql, map, args);
            }
            public void Dispose() { inner.Dispose(); owner.Active--; }
        }
    }
}
