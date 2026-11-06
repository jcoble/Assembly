using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mirror;
using Assembly.Engine.Reading;
using Assembly.Application.Common.Database;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine;

public sealed class MirrorApplyTests : IDisposable
{
    private readonly TopicSet _topics = new();
    private readonly EngineFixture _f;
    public MirrorApplyTests() { _f = new(topics: _topics); Write(s => MirrorApply.Open(s, "w", "workbox-test", 1000)); Snapshot(0, []); }
    private T Write<T>(Func<IWriteScope, T> f) => _f.Db.Writer.Write("test.mirror", f);
    private MirrorCursor Cursor() => Write(s => MirrorApply.Open(s, "w", "workbox-test", 1000));
    private static JsonElement Row(int version, string id = "s", string machine = "w") => JsonSerializer.Deserialize($$"""{"entityKind":"session","id":"{{id}}","machine":"{{machine}}","version":{{version}},"title":"version {{version}}"}""", EngineJson.Default.JsonElement);
    private static EngineEvent Event(long seq, string machine = "w") => new(machine, seq, "changed", "session", "s", seq, 1000, false, new(null, null, null, null, "idle", null));
    private void Snapshot(long cut, JsonElement[] rows) => Write(s => { MirrorApply.BeginSnapshot(s, "w", 1000); MirrorApply.SnapshotPage(s, "w", rows); return MirrorApply.EndSnapshot(s, "w", cut, cut == 0 ? null : new("changed", "s", cut), 1000); });
    private MirrorCursor Events(long after, long through, EngineEvent[] events, JsonElement[]? rows = null) => Write(s => MirrorApply.Events(s, "w", after, through, new("changed", "s", through), events, rows ?? [], 1000));
    private long Visible() => _f.Number("SELECT COUNT(*) FROM engine_mirror_row r JOIN engine_mirror m ON m.machine=r.machine AND m.generation=r.generation WHERE m.machine='w'");

    [Fact] public void Duplicate_events_apply_once() { Events(0, 5, Enumerable.Range(1, 5).Select(i => Event(i)).ToArray()); Events(5, 5, [Event(5)]); Assert.Equal(5, _f.Count("engine_event WHERE machine='w'")); }
    [Fact]
    public void Duplicate_page_does_not_repeat_history()
    {
        var page = Enumerable.Range(1, 5).Select(i => Event(i)).ToArray();
        Events(0, 5, page);
        Assert.Throws<MirrorGap>(() => Events(0, 5, page));
        Assert.Equal(5, _f.Count("engine_event WHERE machine='w'"));
        Assert.Equal(5, Cursor().Through);
    }
    [Fact] public void Gap_throws_and_writes_nothing() { Events(0, 5, Enumerable.Range(1, 5).Select(i => Event(i)).ToArray()); Assert.Throws<MirrorGap>(() => Events(7, 9, [Event(8), Event(9)], [Row(9)])); Assert.Equal(5, Cursor().Through); Assert.Equal(0, Visible()); }
    [Fact] public void Missing_event_in_page_rolls_back() { Assert.Throws<MirrorGap>(() => Events(0, 3, [Event(1), Event(3)], [Row(3)])); Assert.Equal(0, _f.Count("engine_event WHERE machine='w'")); Assert.Equal(0, Cursor().Through); }
    [Fact] public void Partial_snapshot_is_invisible_until_complete() { Snapshot(1, [Row(1)]); Write(s => { MirrorApply.BeginSnapshot(s, "w", 1000); MirrorApply.SnapshotPage(s, "w", [Row(2, "new"), Row(2, "other")]); return 0; }); Assert.Equal(1, Visible()); Write(s => MirrorApply.EndSnapshot(s, "w", 2, new("changed", "s", 2), 1000)); Assert.Equal(2, Visible()); Assert.Equal(2, _f.Count("engine_mirror_row")); }
    [Fact] public void Snapshot_drops_history_and_begin_clears_interrupted_generation() { Events(0, 1, [Event(1)]); Write(s => { MirrorApply.BeginSnapshot(s, "w", 1000); MirrorApply.SnapshotPage(s, "w", [Row(1)]); MirrorApply.BeginSnapshot(s, "w", 1000); return 0; }); Assert.Equal(0, _f.Count("engine_event WHERE machine='w'")); Assert.Equal(0, _f.Count("engine_mirror_row")); Write(s => MirrorApply.EndSnapshot(s, "w", 180, new("changed", "s", 180), 1000)); Events(180, 190, Enumerable.Range(181, 10).Select(i => Event(i)).ToArray()); Assert.Equal(10, _f.Count("engine_event WHERE machine='w'")); Assert.Equal(180, _f.Number("SELECT retained_after FROM engine_mirror WHERE machine='w'")); }
    [Fact] public void Retiring_a_mirror_deletes_rows_and_events() { Events(0, 1, [Event(1)], [Row(1)]); Write(s => MirrorApply.Open(s, "new", "workbox-test", 1000)); Assert.Equal("retired", _f.Text("SELECT state FROM engine_mirror WHERE machine='w'")); Assert.Equal(0, _f.Count("engine_mirror_row WHERE machine='w'")); Assert.Equal(0, _f.Count("engine_event WHERE machine='w'")); }
    [Fact] public void Older_row_version_never_overwrites_newer() { Events(0, 1, [Event(1)], [Row(4)]); Events(1, 2, [Event(2)], [Row(2)]); Assert.Equal(4, _f.Number("SELECT version FROM engine_mirror_row WHERE machine='w'")); }
    [Fact] public void Foreign_event_or_row_rolls_back_entire_page() { Assert.Throws<ArgumentException>(() => Events(0, 2, [Event(1), Event(2, "foreign")], [Row(2)])); Assert.Throws<ArgumentException>(() => Events(0, 1, [Event(1)], [Row(1, machine: "foreign")])); Assert.Equal(0, Cursor().Through); Assert.Equal(0, _f.Count("engine_event")); }
    [Fact] public void Mirror_apply_never_transitions_or_queues_effects() { _f.SeedSession(); var before = _f.Text("SELECT json_object('version',version,'state',state) FROM engine_session WHERE id='s'"); Events(0, 1, [Event(1)], [Row(5)]); Snapshot(1, [Row(9)]); Assert.Equal(before, _f.Text("SELECT json_object('version',version,'state',state) FROM engine_session WHERE id='s'")); Assert.Equal(0, _f.Count("engine_outbox")); Assert.Equal(0, _f.Count("engine_command_receipt")); }
    [Fact] public void Reset_and_cursor_publish_after_commit_only() { var notices = new List<JsonElement>(); var sub = _topics.Subscribe("__engine_mirrored", "w", _ => { }, (_, json) => notices.Add(JsonSerializer.Deserialize(json, EngineJson.Default.JsonElement))); try { Snapshot(10, [Row(1)]); Assert.True(Assert.Single(notices).GetProperty("reset").GetBoolean()); Assert.Equal(10, notices[0].GetProperty("through").GetInt64()); Assert.Equal(new CursorAt("changed", "s", 10), Cursor().At); Assert.Throws<InvalidOperationException>(() => Write<int>(s => { MirrorApply.BeginSnapshot(s, "w", 1000); MirrorApply.EndSnapshot(s, "w", 11, null, 1000); throw new InvalidOperationException(); })); Assert.Single(notices); Assert.Equal(10, Cursor().Through); } finally { _topics.Unsubscribe(sub); } }
    public void Dispose() => _f.Dispose();
}
