using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Topics = Assembly.Infrastructure.Topics.Topics;
namespace Assembly.Tests.Engine;

public sealed class MessageUpgradeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static EffectReceipt Delivered(string state = "next_turn", string? reason = null) => new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new MessageEffectData(state, "real-turn", reason), EngineJson.Default.MessageEffectData), null);
    private static EffectReceipt Unavailable() => new(EffectDisposition.Unavailable, JsonSerializer.SerializeToElement((string?)null), "offline");
    private sealed class OldDb : IDisposable
    {
        private readonly TempDatabase _file = new();
        public Topics Topics { get; } = new();
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(10000));
        public AppDatabase Db
        {
            get;
        }
        public string Machine => Text("SELECT machine_id FROM engine_meta")!;
        public OldDb(int version, bool crash = false)
        {
            Db = AppDatabase.Open(_file.Path, Time, Topics);
            RowStore.Open(Db);
            Db.Writer.Write("test.old-producer", s => s.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", $"old-v{version}{(crash ? "-crash" : "")}.sql"))));
        }
        public void Upgrade() => EngineSchema.Open(Db, 10000);
        public string? Text(string sql) => Db.QueryFirst(sql, r => r.IsNull(0) ? null : r.Text(0), default);
        public long Number(string sql) => Db.QueryFirst(sql, r => r.Long(0), default);
        public string Rows(string sql)
        {
            using var c = new SqliteConnection("Data Source=" + _file.Path);
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = sql;
            using var r = q.ExecuteReader();
            List<object?[]> rows = [];
            while (r.Read())
            {
                var row = new object?[r.FieldCount];
                for (int i = 0; i < row.Length; i++)
                    row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return JsonSerializer.Serialize(rows);
        }
        public string Snapshot()
        {
            var tables = Db.Query("SELECT name FROM sqlite_master WHERE type='table' AND (name LIKE 'engine_%' OR name IN ('sessions','rows')) ORDER BY name", r => r.Text(0), default);
            return Rows("SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY name") + string.Join("", tables.Select(t =>
            {
                var columns = Db.Query($"PRAGMA table_info([{t}])", r => r.Text(1), default);
                return Rows($"SELECT {string.Join(',', columns)} FROM [{t}] ORDER BY rowid");
            }));
        }
        public CommandResult Apply(EngineCommand command, string id, long version = 0) => Db.Writer.Write("test.command", s => new EngineCommands(Machine, new(() => 1, () => 5)).Apply(s, new(Guid.NewGuid(), Machine, new(ActorKind.System, "adapter"), id, version, 0, 10000), command));
        public void Write(string sql) => Db.Writer.Write("test.arrange", s => s.Execute(sql));
        public void Dispose()
        {
            Db.Dispose();
            _file.Dispose();
        }
    }
    private static async Task Until(Channel<EngineEvent> events, Func<EngineEvent, bool> predicate)
    {
        using var stop = new CancellationTokenSource(Timeout);
        while (!predicate(await events.Reader.ReadAsync(stop.Token)))
        {
        }
    }
    private static async Task State(OldDb f, Channel<EngineEvent> events, string id, string state)
    {
        if (f.Text($"SELECT state FROM engine_outbox WHERE id='{id}'") != state)
            await Until(events, e => e.EntityKind == "effect" && e.EntityId == id && e.Data.State == state);
    }
    private static FakeEffects Fake(Func<Effect, CancellationToken, Task<EffectReceipt>>? execute = null, Func<Effect, CancellationToken, Task<EffectReceipt>>? reconcile = null) => new() { Execute = execute ?? ((_, _) => Task.FromResult(Unavailable())), Reconcile = reconcile ?? ((_, _) => Task.FromResult(Unavailable())) };
    private static Effect[] Attempts(FakeEffects fake)
    {
        // FakeEffects adds attempts while holding this same stable list lock.
        var attempts = fake.Attempts;
        lock (attempts) return attempts.ToArray();
    }

    private static string[] Executed(FakeEffects fake)
    {
        lock (fake.Attempts) return fake.ExecutedIds.ToArray();
    }

    private static string[] Reconciled(FakeEffects fake)
    {
        lock (fake.Attempts) return fake.ReconciledIds.ToArray();
    }

    private static IDisposable Observe(EngineRuntime runtime, out Channel<EngineEvent> events)
    {
        var channel = Channel.CreateUnbounded<EngineEvent>();
        events = channel;
        return runtime.Publisher.Register(e => channel.Writer.TryWrite(e), out _);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    public void Actual_old_fixture_upgrade_preserves_full_body_and_sender_title_label_at_nul(int version, bool bodyNul)
    {
        using var f = new OldDb(version);
        var title = bodyNul ? "Accepted title" : new string('t', 1024) + "\0LABEL_SUFFIX😀";
        var body = bodyNul ? new string('b', 1024) + "\0BODY_SUFFIX😀" : "Full ordinary body";
        f.Db.Writer.Write("test.old-full-bytes", scope =>
        {
            scope.Execute("UPDATE engine_session SET title=CAST($title AS TEXT) WHERE id='child/x'", new SqlArg("$title", System.Text.Encoding.UTF8.GetBytes(title)));
            scope.Execute("UPDATE engine_message SET from_session_id='child/x',sender_kind='session',text=CAST($body AS TEXT) WHERE id='inflight'", new SqlArg("$body", System.Text.Encoding.UTF8.GetBytes(body)));
            return scope.Execute("UPDATE engine_outbox SET payload_json=json_set(payload_json,'$.fromSessionId','child/x','$.text',CAST($body AS TEXT)) WHERE id='send/inflight'", new SqlArg("$body", System.Text.Encoding.UTF8.GetBytes(body)));
        });
        var original = JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox WHERE id='send/inflight'")!, EngineJson.Default.SendMessage)!;
        Assert.Equal(body, original.Text); Assert.Equal("child/x", original.FromSessionId);
        var receipts = f.Rows("SELECT command_id,fingerprint,result_json FROM engine_command_receipt ORDER BY command_id");
        var attempts = f.Rows("SELECT attempt,receipt_json,last_error FROM engine_outbox WHERE id='send/inflight'");
        f.Upgrade();
        var actual = JsonSerializer.Deserialize(f.Text("SELECT payload_json FROM engine_outbox WHERE id='send/inflight'")!, EngineJson.Default.DeliverMessage)!;
        Assert.Equal(body, actual.Text); Assert.Equal($"{title} (child/x)", actual.SenderLabel);
        Assert.Equal("send/inflight", actual.ClientId);
        Assert.Equal(receipts, f.Rows("SELECT command_id,fingerprint,result_json FROM engine_command_receipt ORDER BY command_id"));
        Assert.Equal(attempts, f.Rows("SELECT attempt,receipt_json,last_error FROM engine_outbox WHERE id='send/inflight'"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    public void Actual_old_fixture_upgrade_strict_bad_body_or_title_rolls_back_all_schema_and_history(int version, bool badTitle)
    {
        using var f = new OldDb(version);
        f.Db.Writer.Write("test.bad-old-utf8", scope => badTitle
            ? scope.Execute("UPDATE engine_session SET title=CAST($bad AS TEXT) WHERE id='child/x'", new SqlArg("$bad", new byte[] { 0xff }))
            : scope.Execute("UPDATE engine_message SET text=CAST($bad AS TEXT) WHERE id='inflight'", new SqlArg("$bad", new byte[] { 0xff })));
        var before = f.Snapshot();
        Assert.Throws<System.Text.DecoderFallbackException>(() => f.Upgrade());
        Assert.Equal(version, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Fresh_is_combined_v3_and_reopen_is_inert()
    {
        using var f = new EngineFixture();
        Assert.Equal(15, f.Number("SELECT schema_version FROM engine_meta"));
        f.SeedSession();
        f.Apply(new SendMessage(null, "s", "hello", "prompt"), "m");
        var before = f.Text("SELECT group_concat(payload_json) FROM engine_outbox");
        var next = f.Number("SELECT next_seq FROM engine_meta");
        var machine = f.Machine;
        f.Reopen();
        Assert.Equal(machine, f.Machine);
        Assert.Equal(next, f.Number("SELECT next_seq FROM engine_meta"));
        Assert.Equal(before, f.Text("SELECT group_concat(payload_json) FROM engine_outbox"));
        Assert.Equal(0, f.Number("SELECT can_steer FROM engine_session"));
        Assert.Null(f.Text("SELECT client_id FROM engine_message"));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Actual_v1_and_machine_v2_preserve_history_and_mirrors(int version)
    {
        using var f = new OldDb(version);
        var machine = f.Machine;
        var next = f.Number("SELECT next_seq FROM engine_meta");
        var history = f.Rows("SELECT id,version,machine,created_ms,updated_ms,task_url,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json FROM engine_event ORDER BY id");
        var receipts = f.Rows("SELECT command_id,fingerprint,result_json FROM engine_command_receipt ORDER BY command_id");
        var stops = f.Rows("SELECT id,version,machine,created_ms,updated_ms,task_url,event_seq,session_id,run_id,kind,payload_json,state,attempt,next_ms,receipt_json,last_error FROM engine_outbox WHERE kind!='send' ORDER BY id");
        var chat = f.Rows("SELECT session_id,next_seq FROM sessions ORDER BY session_id") + f.Rows("SELECT session_id,seq,run_id,item_id,kind,payload,created_at FROM rows ORDER BY session_id,seq");
        var attempts = f.Rows("SELECT step_id,attempt,state,started_ms,ended_ms,proof_json,reason,late FROM engine_step_attempt") + f.Rows("SELECT id,step_id,attempt,kind,at_ms,data_json,late FROM engine_step_fact");
        var terminal = f.Rows("SELECT id,version,state,provider_turn_id,reason,text FROM engine_message WHERE state NOT IN ('queued','held') ORDER BY id");
        var mirrors = version == 2 ? f.Rows("SELECT machine,name,generation,through,at_json,retained_after,state,updated_ms FROM engine_mirror") + f.Rows("SELECT machine,entity_kind,entity_id,generation,version,row_json FROM engine_mirror_row") : "";
        f.Upgrade();
        Assert.Equal(15, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(machine, f.Machine);
        Assert.Equal(history, f.Rows($"SELECT id,version,machine,created_ms,updated_ms,task_url,seq,kind,entity_kind,entity_id,entity_version,at_ms,late,data_json FROM engine_event WHERE machine!='{machine}' OR seq<{next} ORDER BY id"));
        Assert.Equal(chat, f.Rows("SELECT session_id,next_seq FROM sessions ORDER BY session_id") + f.Rows("SELECT session_id,seq,run_id,item_id,kind,payload,created_at FROM rows ORDER BY session_id,seq"));
        Assert.Equal(attempts, f.Rows("SELECT step_id,attempt,state,started_ms,ended_ms,proof_json,reason,late FROM engine_step_attempt") + f.Rows("SELECT id,step_id,attempt,kind,at_ms,data_json,late FROM engine_step_fact"));
        Assert.Equal(receipts, f.Rows("SELECT command_id,fingerprint,result_json FROM engine_command_receipt ORDER BY command_id"));
        Assert.Equal(stops, f.Rows("SELECT id,version,machine,created_ms,updated_ms,task_url,event_seq,session_id,run_id,kind,payload_json,state,attempt,next_ms,receipt_json,last_error FROM engine_outbox WHERE kind!='send' ORDER BY id"));
        Assert.Equal(terminal, f.Rows("SELECT id,version,state,provider_turn_id,reason,text FROM engine_message WHERE id LIKE 'terminal-%' ORDER BY id"));
        Assert.Equal(1, f.Number("SELECT retained_after FROM engine_meta"));
        Assert.Equal(2, f.Number("SELECT snapshot_seq FROM engine_meta"));
        Assert.Equal(next + f.Number($"SELECT COUNT(*) FROM engine_event WHERE machine='{machine}' AND seq>={next}"), f.Number("SELECT next_seq FROM engine_meta"));
        if (version == 2)
            Assert.Equal(mirrors, f.Rows("SELECT machine,name,generation,through,at_json,retained_after,state,updated_ms FROM engine_mirror") + f.Rows("SELECT machine,entity_kind,entity_id,generation,version,row_json FROM engine_mirror_row"));
        var after = f.Snapshot();
        f.Upgrade();
        Assert.Equal(after, f.Snapshot());
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Pending_upgrade_releases_once_per_recipient_with_same_client_identity(int version)
    {
        using var f = new OldDb(version);
        var retry = f.Rows("SELECT attempt,receipt_json,last_error,event_seq,rowid,payload_json FROM engine_outbox WHERE id='send/transient'");
        f.Upgrade();
        Assert.Equal(retry, f.Rows("SELECT attempt,receipt_json,last_error,event_seq,rowid,payload_json FROM engine_outbox WHERE id='send/transient'"));
        var ledger = new HashSet<string>();
        var fake = Fake((e, _) => { if (e.Kind != "send" || e.SessionId is not ("pending" or "transient")) return Task.FromResult(Unavailable()); var intent = e.Payload.Deserialize(EngineJson.Default.DeliverMessage)!; Assert.True(ledger.Add(intent.ClientId)); return Task.FromResult(Delivered()); });
        await using var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 1, () => 5), fake);
        using var consumer = Observe(runtime, out var events);
        await State(f, events, "send/pending/1", "done");
        await State(f, events, "send/transient/1", "done");
        Assert.Contains("send/pending", ledger);
        Assert.Contains("send/transient", ledger);
        Assert.Equal(0, f.Number("SELECT attempt FROM engine_message WHERE id='pending-2'"));
        Assert.Equal("running", f.Text("SELECT state FROM engine_session WHERE id='pending'"));
        Assert.True(f.Apply(new SetSessionState("idle", null, null), "pending", f.Number("SELECT version FROM engine_session WHERE id='pending'")).Applied);
        await State(f, events, "send/pending-2/1", "done");
        Assert.Equal(3, ledger.Count);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Crash_after_old_external_success_reconciles_same_identity_without_execute(int version)
    {
        using var f = new OldDb(version, crash: true);
        var old = f.Rows("SELECT id,rowid,event_seq,attempt,version,state,receipt_json FROM engine_outbox WHERE id='send/inflight'");
        f.Upgrade();
        Assert.Equal(old, f.Rows("SELECT id,rowid,event_seq,attempt,version,state,receipt_json FROM engine_outbox WHERE id='send/inflight'"));
        var fake = Fake(reconcile: (e, _) => { if (e.Id != "send/inflight") return Task.FromResult(Unavailable()); var intent = e.Payload.Deserialize(EngineJson.Default.DeliverMessage)!; Assert.Equal("send/inflight", intent.ClientId); Assert.Equal("inflight", intent.MessageId); return Task.FromResult(Delivered()); });
        await using var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 1, () => 5), fake);
        using var consumer = Observe(runtime, out var events);
        await State(f, events, "send/inflight", "done");
        Assert.DoesNotContain("send/inflight", Executed(fake));
        Assert.Contains("send/inflight", Reconciled(fake));
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='inflight'"));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Unknown_unavailable_stays_reserved_until_availability_then_reconciles(int version)
    {
        using var f = new OldDb(version);
        f.Upgrade();
        var receipt = f.Text("SELECT receipt_json FROM engine_outbox WHERE id='send/unknown-unavailable'");
        var fake = Fake(reconcile: (e, _) => Task.FromResult(e.Id == "send/unknown-unavailable" ? Delivered() : Unavailable()));
        await using var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 1, () => 5), fake);
        using var consumer = Observe(runtime, out var events);
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id='unknown-unavailable'"));
        Assert.Equal(receipt, f.Text("SELECT receipt_json FROM engine_outbox WHERE id='send/unknown-unavailable'"));
        f.Time.Advance(TimeSpan.FromDays(1));
        Assert.DoesNotContain("send/unknown-unavailable", Reconciled(fake));
        runtime.EffectsAvailable();
        await State(f, events, "send/unknown-unavailable", "done");
        Assert.DoesNotContain("send/unknown-unavailable", Executed(fake));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Known_unavailable_push_releases_through_current_guards(int version)
    {
        using var f = new OldDb(version);
        f.Upgrade();
        Assert.True(f.Apply(new OpenNeed("unavailable", null, null, "question", "Wait?", [], null, null, null), "new-need").Applied);
        var fake = Fake((e, _) => Task.FromResult(e.SessionId == "unavailable" ? Delivered() : Unavailable()));
        await using var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 1, () => 5), fake);
        using var consumer = Observe(runtime, out var events);
        runtime.EffectsAvailable();
        await State(f, events, "send/unavailable", "dead");
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='unavailable'"));
        Assert.DoesNotContain(Attempts(fake), e => e.SessionId == "unavailable");
        // Resolving the last need releases through current guards without another state push.
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "window"), "new-need", 1, 0, 10000);
        Assert.True(f.Db.Writer.Write("test.resolve", s => new EngineCommands(f.Machine, new(() => 1, () => 5)).Apply(s, context, new ResolveNeed("dismissed", null))).Applied);
        await State(f, events, "send/unavailable/2", "done");
        Assert.Single(Attempts(fake), e => e.SessionId == "unavailable");
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Authoritative_not_delivered_requeues_once_but_missing_ack_never_resends(int version)
    {
        using var f = new OldDb(version);
        f.Upgrade();
        int reconciliations = 0;
        var fake = Fake((e, _) => { Assert.NotEqual("send/inflight", e.Id); return Task.FromResult(e.SessionId == "inflight" ? Delivered() : Unavailable()); }, (e, _) =>
        {
            if (e.Id == "send/inflight")
                return Task.FromResult(Delivered("queued", "not_delivered"));
            if (e.Id == "send/uncertain")
            {
                return Task.FromResult(Interlocked.Increment(ref reconciliations) == 1 ? Unavailable() : Delivered());
            }
            return Task.FromResult(Unavailable());
        });
        await using var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 1, () => 5), fake);
        using var consumer = Observe(runtime, out var events);
        await State(f, events, "send/inflight/2", "done");
        await State(f, events, "send/uncertain", "unavailable");
        Assert.Equal(1, reconciliations);
        Assert.Equal(1, f.Number("SELECT in_flight FROM engine_message WHERE id='uncertain'"));
        Assert.DoesNotContain("send/uncertain", Executed(fake));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM engine_outbox WHERE id='send/uncertain/2'"));
        Assert.Equal("send/inflight", Attempts(fake).Single(e => e.Id == "send/inflight/2").Payload.Deserialize(EngineJson.Default.DeliverMessage)!.ClientId);
        runtime.EffectsAvailable();
        await State(f, events, "send/uncertain", "done");
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='uncertain'"));
        Assert.DoesNotContain("send/uncertain", Executed(fake));
    }
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Held_interrupt_upgrade_waits_for_old_stop_ack(int version, bool ended)
    {
        using var f = new OldDb(version);
        f.Upgrade();
        var ack = new TaskCompletionSource<EffectReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = Fake((e, ct) => e.Id == "message-stop/held" ? ack.Task.WaitAsync(ct) : Task.FromResult(e.SessionId == "held" ? Delivered() : Unavailable()));
        await using var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 1, () => 5), fake);
        using var consumer = Observe(runtime, out var events);
        await State(f, events, "message-stop/held", "inflight");
        Assert.Equal("held", f.Text("SELECT state FROM engine_message WHERE id='held'"));
        Assert.DoesNotContain(Attempts(fake), e => e.Kind == "send" && e.SessionId == "held");
        ack.SetResult(new(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(new SessionEffectData(ended ? "ended" : "idle", null, null), EngineJson.Default.SessionEffectData), null));
        if (ended)
        {
            await State(f, events, "message-stop/held", "done");
            Assert.Equal("failed", f.Text("SELECT state FROM engine_message WHERE id='held'"));
            Assert.Equal("recipient_ended", f.Text("SELECT reason FROM engine_message WHERE id='held'"));
            Assert.DoesNotContain(Attempts(fake), e => e.Kind == "send" && e.SessionId == "held");
        }
        else
        {
            await State(f, events, "send/held/1", "done");
            Assert.Single(Attempts(fake), e => e.Kind == "send" && e.SessionId == "held");
        }
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Failed_effect_and_terminal_message_leave_no_runnable_send_or_stuck_flag(int version)
    {
        using var f = new OldDb(version);
        var terminal = f.Rows("SELECT id,version,machine,created_ms,updated_ms,task_url,event_seq,session_id,run_id,kind,payload_json,state,attempt,next_ms,receipt_json,last_error FROM engine_outbox WHERE id='send/terminal-unsupported'");
        f.Upgrade();
        Assert.Equal("failed", f.Text("SELECT state FROM engine_message WHERE id='failed'"));
        Assert.Equal("permanent", f.Text("SELECT reason FROM engine_message WHERE id='failed'"));
        Assert.Equal(0, f.Number("SELECT in_flight FROM engine_message WHERE id='failed'"));
        Assert.Equal("dead", f.Text("SELECT state FROM engine_outbox WHERE id='send/terminal-pending'"));
        Assert.Equal(terminal, f.Rows("SELECT id,version,machine,created_ms,updated_ms,task_url,event_seq,session_id,run_id,kind,payload_json,state,attempt,next_ms,receipt_json,last_error FROM engine_outbox WHERE id='send/terminal-unsupported'"));
        Assert.Equal("not_due", f.Apply(new ClaimEffect(), "send/inflight", 1).Error);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Old_command_receipts_replay_without_new_delivery_or_rows(int version)
    {
        using var f = new OldDb(version);
        f.Upgrade();
        var before = f.Snapshot();
        void Replay(int n, string id, long v, bool system, EngineCommand command)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(n).CopyTo(bytes, 0);
            var guid = new Guid(bytes);
            var context = new CommandContext(guid, f.Machine, new(system ? ActorKind.System : ActorKind.Window, system ? "adapter" : "window"), id, v, 0, 1000);
            var stored = f.Text($"SELECT result_json FROM engine_command_receipt WHERE command_id='{guid:N}'");
            var actual = f.Db.Writer.Write("test.replay", s => new EngineCommands(f.Machine, new(() => 1, () => 5)).Apply(s, context, command));
            Assert.Equal(stored, JsonSerializer.Serialize(actual, EngineJson.Default.CommandResult));
        }
        Replay(1, "pending", 0, false, new SendMessage(null, "pending", "body pending", "prompt"));
        Replay(15, "send/inflight", 0, true, new ClaimEffect());
        Replay(24, "send/transient", 1, true, new CompleteEffect(new(EffectDisposition.TransientFailure, JsonSerializer.SerializeToElement((string?)null), "retry")));
        Replay(34, "terminal-pending", 1, true, new MessageOutcome("unsupported", "old-turn", "historical"));
        Assert.Equal(before, f.Snapshot());
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Child_ended_slash_id_converts_and_confirms_from_payload(int version)
    {
        using var f = new OldDb(version);
        f.Upgrade();
        var fake = Fake((e, _) => Task.FromResult(e.SessionId == "child-parent" ? Delivered() : Unavailable()));
        await using var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 1, () => 5), fake);
        using var consumer = Observe(runtime, out var events);
        await State(f, events, "send/child-ended/child/x/1", "done");
        Assert.Equal("next_turn", f.Text("SELECT state FROM engine_message WHERE id='child-ended/child/x'"));
        Assert.Equal("send/child-ended/child/x", Attempts(fake).Single(e => e.SessionId == "child-parent").Payload.Deserialize(EngineJson.Default.DeliverMessage)!.ClientId);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Late_failure_rolls_back_DDL_send_conversion_journal_and_version_then_retry(int version)
    {
        using var f = new OldDb(version);
        f.Write("CREATE TRIGGER test_block_engine_schema BEFORE UPDATE OF schema_version ON engine_meta BEGIN SELECT RAISE(ABORT,'migration blocked'); END;");
        var before = f.Snapshot();
        var error = Assert.Throws<SqliteException>(f.Upgrade);
        Assert.Contains("migration blocked", error.Message);
        Assert.Equal(before, f.Snapshot());
        f.Write("DROP TRIGGER test_block_engine_schema");
        f.Upgrade();
        Assert.Equal(15, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(1, f.Number("SELECT COUNT(*) FROM engine_event WHERE entity_id='send/pending' AND json_extract(data_json,'$.state')='dead'"));
        var fake = Fake((e, _) => Task.FromResult(e.SessionId == "pending" ? Delivered() : Unavailable()));
        await using var runtime = EngineRuntime.Start(f.Db, f.Topics, f.Time, new(() => 1, () => 5), fake);
        using var consumer = Observe(runtime, out var events);
        await State(f, events, "send/pending/1", "done");
        Assert.Single(Attempts(fake), e => e.SessionId == "pending");
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Future_version_16_refuses_without_mutation_and_malformed_payload_rolls_back(int version)
    {
        using var f = new OldDb(version);
        foreach (var unsupported in new[] { 0, -1, 16 })
        {
            f.Write($"UPDATE engine_meta SET schema_version={unsupported}");
            var unchanged = f.Snapshot();
            Assert.Throws<InvalidOperationException>(f.Upgrade);
            Assert.Equal(unchanged, f.Snapshot());
        }
        f.Write($"UPDATE engine_meta SET schema_version={version};UPDATE engine_outbox SET payload_json='{{}}' WHERE id='send/inflight'");
        var before = f.Snapshot();
        Assert.Throws<InvalidOperationException>(f.Upgrade);
        Assert.Equal(before, f.Snapshot());
    }
}
