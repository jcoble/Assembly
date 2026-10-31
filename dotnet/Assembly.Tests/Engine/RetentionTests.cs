using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Engine;
using Assembly.Application.Features.Engine.Contracts;
using Assembly.Application.Features.Engine.Persistence;
using Assembly.Application.Features.Engine.Reading;
using Assembly.Application.Features.Engine.Recording;
using Assembly.Application.Features.Engine.Retention;
using Assembly.Infrastructure.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine;

public sealed class RetentionTests
{
    private const long Week = 7L * 24 * 60 * 60 * 1000;
    private const string Bytes = "COALESCE(length(CAST(input_preview AS BLOB)),0)+COALESCE(length(CAST(output_preview AS BLOB)),0)";

    private static void Events(EngineFixture f, int high) => f.Db.Writer.Write("seed.events", s =>
    {
        var changed = s.Execute("""
        WITH RECURSIVE numbers(n) AS (SELECT (SELECT next_seq FROM engine_meta) UNION ALL SELECT n+1 FROM numbers WHERE n<$high)
        INSERT INTO engine_event(id,version,machine,created_ms,updated_ms,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json)
        SELECT 'event-'||n,0,$machine,0,0,n,'changed','run','r',0,0,0,'{}' FROM numbers WHERE n<=$high;
        UPDATE engine_meta SET next_seq=$high+1;
        """, new("$high", high), new("$machine", f.Machine));
        s.Publish("__engine_committed", null, (long)high, EngineJson.Default.Int64);
        return changed;
    });

    private static void Tool(EngineFixture f, string id, long start, long? end, string preview) =>
        f.Db.Writer.Write("seed.tool", s => s.Execute("""
            INSERT INTO engine_tool_call(id,version,machine,created_ms,updated_ms,session_id,epoch,boundary,boundary_id,
                tool,input_preview,output_preview,input_bytes,output_bytes,started_ms,ended_ms,outcome,late)
            VALUES($id,0,$machine,0,0,'s',0,'provider',$id,'read',$preview,$preview,99999,99999,$start,$end,'ok',0)
            """, new("$id", id), new("$machine", f.Machine), new("$preview", preview), new("$start", start), new("$end", end)));

    [Fact]
    public void Reader_ack_limits_compaction_and_sequences_never_reuse()
    {
        using var f = new EngineFixture();
        Events(f, 11000);
        var result = f.Db.Writer.Write("retain", s => EngineRetention.Apply(s, 1000, 11000, 700));
        Assert.Equal(700, result.RetainedAfter);
        Assert.Equal(700, result.RemovedEvents);
        Assert.Equal(11001, f.Number("SELECT next_seq FROM engine_meta"));
        Assert.Equal(11000, f.Number("SELECT snapshot_seq FROM engine_meta"));
        var reader = new EngineReader(f.Db, f.Machine);
        Assert.True(reader.Since("runs", null, 699, default).SnapshotRequired);
        Assert.False(reader.Since("runs", null, 700, default).SnapshotRequired);
        Events(f, 11001);
        Assert.Equal(11001, f.Number("SELECT MAX(seq) FROM engine_event"));
    }

    [Theory]
    [InlineData(10999, 10999, 10999, 0)]
    [InlineData(11000, 500, 700, 500)]
    [InlineData(11000, 11000, 11000, 1000)]
    public void Compaction_respects_trigger_publisher_and_newest_events(int high, long published, long ack, long floor)
    {
        using var f = new EngineFixture();
        Events(f, high);
        var result = f.Db.Writer.Write("retain", s => EngineRetention.Apply(s, 1000, published, ack));
        Assert.Equal(floor, result.RetainedAfter);
        Assert.Equal(high - floor, f.Count("engine_event"));
    }

    [Fact]
    public void Caps_preserve_decisions_and_outcomes()
    {
        using var f = new EngineFixture();
        f.SeedSession(); f.SeedRun(state: "succeeded"); f.SeedStep(state: "succeeded");
        f.Db.Writer.Write("seed.history", s => s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state,answer_json)
            VALUES('need',0,$machine,0,0,'s','question','Decision','[]','answered','{"answer":"yes"}');
            INSERT INTO engine_pr(id,version,machine,created_ms,updated_ms,run_id,repo,state,url)
            VALUES('pr',0,$machine,0,0,'r','repo','merged','https://example.com/pr/1');
            INSERT INTO engine_step_attempt VALUES('step',1,'succeeded',0,1,'{}','done',0);
            INSERT INTO engine_step_fact VALUES('fact','step',1,'outcome',1,'{}',0);
            INSERT INTO engine_command_receipt VALUES('receipt','hash','{}');
            INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,kind,payload_json,state,attempt,receipt_json)
            VALUES('effect',0,$machine,0,0,1,'send','{}','done',1,'{}');
            """, new SqlArg("$machine", f.Machine)));
        Tool(f, "expired", 0, 1000, "old");
        Tool(f, "active", 1001, null, new string('界', 1365) + "x"); // exactly 4096 UTF-8 bytes
        Tool(f, "oversize", 1002, null, new string('界', 2000));
        f.Db.Writer.Write("seed.aggregate", s => s.Execute("""
            WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<2100)
            INSERT INTO engine_tool_call(id,version,machine,created_ms,updated_ms,session_id,epoch,boundary,boundary_id,
                tool,input_preview,output_preview,input_bytes,started_ms,late)
            SELECT 'bulk-'||n,0,$machine,0,0,'s',0,'provider','bulk-'||n,'read',$preview,$preview,123,2000+n,0 FROM numbers
            """, new("$machine", f.Machine), new("$preview", new string('x', 4096))));
        var tables = new[] { "session", "run", "step", "step_attempt", "step_fact", "needs_me", "pr", "command_receipt", "outbox", "tool_call" };
        var before = tables.Select(t => f.Count("engine_" + t)).ToArray();
        var beforeBytes = f.Number($"SELECT SUM({Bytes}) FROM engine_tool_call");
        var result = f.Db.Writer.Write("retain", s => EngineRetention.Apply(s, Week + 1000, 0, 0));
        var previewBytes = f.Number($"SELECT SUM({Bytes}) FROM engine_tool_call");
        Assert.True(previewBytes <= 16 * 1024 * 1024);
        Assert.Equal(beforeBytes - previewBytes, result.ClearedPreviewBytes);
        Assert.Equal(0, f.Number($"SELECT {Bytes} FROM engine_tool_call WHERE id='expired'"));
        Assert.Equal(0, f.Number($"SELECT COUNT(*) FROM engine_tool_call WHERE length(CAST(input_preview AS BLOB))>4096 OR length(CAST(output_preview AS BLOB))>4096"));
        Assert.Equal("succeeded", f.State("engine_step", "step"));
        Assert.Equal("answered", f.State("engine_needs_me", "need"));
        Assert.Equal("merged", f.State("engine_pr", "pr"));
        Assert.Equal(before, tables.Select(t => f.Count("engine_" + t)).ToArray());
        Assert.Equal("{\"answer\":\"yes\"}", f.Text("SELECT answer_json FROM engine_needs_me"));
        Assert.Equal("https://example.com/pr/1", f.Text("SELECT url FROM engine_pr"));
        Assert.Equal("{}", f.Text("SELECT receipt_json FROM engine_outbox"));
    }

    [Fact]
    public void Exact_age_boundary_uses_end_or_start_and_UTF8_cap_preserves_characters()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Tool(f, "ended", 0, 1000, "old");
        Tool(f, "running", 1001, null, new string('界', 1365) + "x");
        Tool(f, "oversize", 1002, null, new string('界', 2000));
        var retention = new EngineRetention(() => 0, () => 0, _ => { });
        Assert.Equal(Week + 1000, f.Db.Writer.Write("deadline", s => retention.NextDeadlineMs(s, 1000)));
        f.Db.Writer.Write("before", s => EngineRetention.Apply(s, Week + 999, 0, 0));
        Assert.Equal("old", f.Text("SELECT input_preview FROM engine_tool_call WHERE id='ended'"));
        Assert.Equal(new string('界', 1365), f.Text("SELECT input_preview FROM engine_tool_call WHERE id='oversize'"));
        f.Db.Writer.Write("boundary", s => EngineRetention.Apply(s, Week + 1000, 0, 0));
        Assert.Null(f.Text("SELECT input_preview FROM engine_tool_call WHERE id='ended'"));
        Assert.Equal(8192, f.Number($"SELECT {Bytes} FROM engine_tool_call WHERE id='running'"));
        Assert.Equal(Week + 1001, f.Db.Writer.Write("deadline", s => retention.NextDeadlineMs(s, Week + 1000)));
    }

    [Fact]
    public void Rollback_and_slow_reader_do_not_lose_history()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Events(f, 11000); Tool(f, "old", 0, null, "preview");
        Assert.Throws<InvalidOperationException>(() => f.Db.Writer.Write<int>("rollback", s =>
        {
            EngineRetention.Apply(s, Week, 11000, 11000);
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal(0, f.Number("SELECT retained_after FROM engine_meta"));
        Assert.Equal(0, f.Number("SELECT snapshot_seq FROM engine_meta"));
        Assert.Equal(11000, f.Count("engine_event"));
        Assert.Equal("preview", f.Text("SELECT input_preview FROM engine_tool_call"));
        var actor = new EngineActor(ActorKind.System, "adapter");
        Assert.True(f.Apply(new ToolStarted(new("s", null, null, null, 0), "p", "secret", "read",
            JsonSerializer.SerializeToElement("sk-secret"), 9), "secret-tool", actor: actor).Applied);
        Assert.True(f.Apply(new ToolEnded(new("s", null, null, null, 0), "ok",
            JsonSerializer.SerializeToElement("sk-secret"), 9), "secret-tool", actor: actor).Applied);
        var persisted = string.Join("", f.Db.Query("SELECT data_json FROM engine_event UNION ALL SELECT COALESCE(input_preview,'')||COALESCE(output_preview,'') FROM engine_tool_call UNION ALL SELECT result_json FROM engine_command_receipt", r => r.Text(0), default));
        Assert.DoesNotContain("sk-secret", persisted);
        Assert.Equal(9, f.Number("SELECT output_bytes FROM engine_tool_call WHERE id='secret-tool'"));
    }

    [Fact]
    public void Open_snapshot_survives_preview_clearing_and_event_compaction()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Tool(f, "tool", 0, null, "preview"); Events(f, 11000);
        using var snapshot = f.Db.OpenSnapshot(default);
        Assert.Equal(11000, snapshot.QueryFirst("SELECT COUNT(*) FROM engine_event", r => r.Long(0)));
        f.Db.Writer.Write("retain", s => EngineRetention.Apply(s, Week, 11000, 11000));
        Assert.Equal(11000, snapshot.QueryFirst("SELECT COUNT(*) FROM engine_event", r => r.Long(0)));
        Assert.Equal("preview", snapshot.QueryFirst("SELECT input_preview FROM engine_tool_call", r => r.Text(0)));
        Assert.Equal(10000, f.Count("engine_event"));
        Assert.Null(f.Text("SELECT input_preview FROM engine_tool_call"));
    }

    private sealed class ObservedRetention(IEngineRetention retention) : IEngineRetention
    {
        public Channel<(long Now, long Floor, long Bytes)> Passes { get; } =
            Channel.CreateUnbounded<(long, long, long)>();
        public void Apply(IWriteScope s, long nowMs) => retention.Apply(s, nowMs);
        public long? NextDeadlineMs(IReadScope s, long nowMs)
        {
            Passes.Writer.TryWrite((nowMs,
                s.QueryFirst("SELECT retained_after FROM engine_meta", r => r.Long(0)),
                s.QueryFirst($"SELECT COALESCE(SUM({Bytes}),0) FROM engine_tool_call", r => r.Long(0))));
            return retention.NextDeadlineMs(s, nowMs);
        }
        public async Task Until(EngineFixture f, Func<(long Now, long Floor, long Bytes), bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!predicate(await Passes.Reader.ReadAsync(timeout.Token))) { }
            // The observed pass still owns the writer until its transaction commits.
            f.Db.Writer.Write("test.retention.commit-gate", _ => 0);
        }
    }

    [Fact]
    public async Task Worker_applies_at_startup_after_publication_and_at_age_deadline()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics); f.SeedSession();
        Tool(f, "expired", -Week, null, "old");
        Tool(f, "live", 1000, null, "live");
        Events(f, 11000);
        var publisher = new TaskCompletionSource<EnginePublisher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ObservedRetention(new EngineRetention(
            () => publisher.Task.GetAwaiter().GetResult().PublishedThrough,
            () => publisher.Task.GetAwaiter().GetResult().MinimumReaderAck,
            through => publisher.Task.GetAwaiter().GetResult().InvalidateReadersBefore(through)));
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, retention: observed);
        publisher.SetResult(runtime.Publisher);
        await observed.Until(f, p => p.Floor > 0 && p.Bytes == 8 && runtime.Publisher.PublishedThrough == 11000);
        Assert.Null(f.Text("SELECT input_preview FROM engine_tool_call WHERE id='expired'"));
        var floor = f.Number("SELECT retained_after FROM engine_meta");
        Assert.InRange(floor, 1, 1000);
        Assert.Equal(11000 - floor, f.Count("engine_event"));
        f.Time.Advance(TimeSpan.FromDays(7));
        await observed.Until(f, p => p.Now == Week + 1000 && p.Bytes == 0);
        Assert.Null(f.Text("SELECT input_preview FROM engine_tool_call WHERE id='live'"));
        // A committed engine write pushes the worker even when it has no pending deadline.
        Assert.True(f.Apply(new ToolStarted(new("s", null, null, null, 0), "p", "new", "read",
            JsonSerializer.SerializeToElement("sk-secret"), 9), "new-tool",
            actor: new(ActorKind.System, "adapter")).Applied);
        await observed.Until(f, p => p.Bytes > 0);
        Assert.DoesNotContain("sk-secret", f.Text("SELECT input_preview FROM engine_tool_call WHERE id='new-tool'")!);
    }

    [Fact]
    public async Task Slow_reader_resets_at_second_backlog_and_next_read_requires_snapshot()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        var publisher = new TaskCompletionSource<EnginePublisher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ObservedRetention(new EngineRetention(
            () => publisher.Task.GetAwaiter().GetResult().PublishedThrough,
            () => publisher.Task.GetAwaiter().GetResult().MinimumReaderAck,
            through => publisher.Task.GetAwaiter().GetResult().InvalidateReadersBefore(through)));
        await using var runtime = EngineRuntime.Start(f.Db, topics, f.Time, () => 5, retention: observed);
        var resets = 0;
        using var reader = runtime.Publisher.TrackReader("slow", 0, () => Interlocked.Increment(ref resets));
        publisher.SetResult(runtime.Publisher);
        Events(f, 21999);
        await observed.Until(f, p => runtime.Publisher.PublishedThrough == 21999);
        Assert.Equal(0, resets);
        Assert.Equal(0, f.Number("SELECT retained_after FROM engine_meta"));
        Events(f, 22000);
        await observed.Until(f, p => p.Floor == 12000);
        Assert.Equal(1, resets);
        Assert.Equal(10000, f.Count("engine_event"));
        Assert.True(runtime.Reader.Since("runs", null, 0, default).SnapshotRequired);
        Assert.False(runtime.Reader.Since("runs", null, 12000, default).SnapshotRequired);
    }

}
