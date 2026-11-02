using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Preferences;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Persistence;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine.Expiry;

public sealed class ExpiryPolicyIntegrationTests
{
    [Theory]
    [InlineData(45)]
    [InlineData(72000)]
    [InlineData(int.MaxValue)]
    public void Commands_share_the_actual_writer_policy_across_ordinary_owner_held_and_system_inserts(int minutes)
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running"); f.SeedSession("child", parentId: "s");
        _ = new PreferenceStore(f.Db); var commands = new EngineCommands(f.Machine, f.Limits, expiryPolicy: new(MessageExpiryPreferences.ReadMinutes));
        var ordinary = Context(f, "ordinary"); var held = Context(f, "held");
        var request = new SendOwnerPrompt("s", "raw\0body", "owner", ["a.png"], null);
        var prepared = OwnerPromptFingerprint.Prepare(f.Machine, request, default);
        var owner = Context(f, OwnerPromptIdentity.MessageId("s", "owner")) with { Actor = new(ActorKind.Window, "owner"), CommandId = OwnerPromptIdentity.CommandId(f.Machine, "s", "owner") };
        f.Db.Writer.Write("test.current-writer", scope =>
        {
            scope.Execute("INSERT INTO preferences VALUES('messageExpiryMinutes',$minutes)", new SqlArg("$minutes", minutes.ToString()));
            Assert.True(commands.Apply(scope, ordinary, new SendMessage(null, "s", "ordinary", "prompt")).Applied);
            Assert.True(commands.Apply(scope, held, new SendMessage(null, "s", "held", "interrupt")).Applied);
            Assert.True(commands.ApplyOwnerPrompt(scope, owner, prepared).Applied);
            Assert.True(commands.Apply(scope, Context(f, "child") with { Actor = new(ActorKind.System, "observer") }, new SetSessionState("ended", null, null)).Applied);
            Assert.True(commands.Apply(scope, Context(f, "need") with { Actor = new(ActorKind.System, "observer") }, new OpenNeed("s", null, null, "question", "Continue?", ["yes", "no"], null, null, null)).Applied);
            var answered = commands.Apply(scope, Context(f, "need") with { ExpectedVersion = 1 }, new ResolveNeed("answered", JsonSerializer.SerializeToElement("yes")));
            Assert.True(answered.Applied, answered.Error); return 0;
        });
        var deadlines = f.Db.Query("SELECT id,expires_ms FROM engine_message ORDER BY id", r => (Id: r.Text(0), Deadline: r.Long(1)), default);
        Assert.Equal(5, deadlines.Count); Assert.All(deadlines, row => Assert.Equal(1000L + (long)minutes * 60000, row.Deadline));
    }

    [Fact]
    public void Actual_command_and_prepared_owner_replays_ignore_corrupt_policy_while_new_insert_rolls_back()
    {
        using var f = new EngineFixture(); f.SeedSession(state: "running"); _ = new PreferenceStore(f.Db);
        var commands = new EngineCommands(f.Machine, f.Limits, expiryPolicy: new(MessageExpiryPreferences.ReadMinutes));
        var context = Context(f, "ordinary"); var command = new SendMessage(null, "s", "body", "prompt");
        var ownerRequest = new SendOwnerPrompt("s", "raw", "client", [], null); var prepared = OwnerPromptFingerprint.Prepare(f.Machine, ownerRequest, default);
        var owner = Context(f, OwnerPromptIdentity.MessageId("s", "client")) with { Actor = new(ActorKind.Window, "owner"), CommandId = OwnerPromptIdentity.CommandId(f.Machine, "s", "client") };
        var first = f.Db.Writer.Write("test.first", s => commands.Apply(s, context, command));
        var raw = f.Db.Writer.Write("test.raw", s => commands.ApplyOwnerPrompt(s, owner, prepared));
        f.Db.Writer.Write("test.corrupt", s => s.Execute("INSERT INTO preferences VALUES('messageExpiryMinutes','not-json')"));
        Assert.Equal(first, f.Db.Writer.Write("test.replay", s => commands.Apply(s, context with { NowMs = 9000000 }, command)));
        Assert.Equal(raw, f.Db.Writer.Write("test.raw-replay", s => commands.ApplyOwnerPrompt(s, owner with { NowMs = 9000000 }, prepared)));
        var count = f.Number("SELECT COUNT(*) FROM engine_command_receipt");
        var error = Assert.Throws<CallFailed>(() => f.Db.Writer.Write("test.atomic-invalid", s =>
        {
            s.Execute("INSERT INTO preferences VALUES('marker','1')"); return commands.Apply(s, Context(f, "new"), command);
        }));
        Assert.Equal(MessageExpiryPreference.Error, error.Message); Assert.Equal(count, f.Number("SELECT COUNT(*) FROM engine_command_receipt"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM preferences WHERE key='marker'")); Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_message"));
    }

    [Fact]
    public async Task Runtime_first_upgrade_reads_configured_snapshot_and_corrupt_startup_never_starts_factory()
    {
        using var file = new TempDatabase(); var topics = new Topics(); using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics); RowStore.Open(db); _ = new PreferenceStore(db);
        db.Writer.Write("test.actual-v5", s => s.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", "old-v5.sql"))));
        db.Writer.Write("test.corrupt", s => s.Execute("INSERT INTO preferences VALUES('messageExpiryMinutes','null')")); var factory = false;
        Assert.Throws<CallFailed>(() => EngineRuntime.Start(db, topics, TimeProvider.System, new(() => 1, () => 5), effectsFactory: _ => { factory = true; throw new InvalidOperationException(); }, readExpiryMinutes: MessageExpiryPreferences.ReadMinutes));
        Assert.False(factory); Assert.Equal(5, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        db.Writer.Write("test.configure", s => s.Execute("UPDATE preferences SET value='45' WHERE key='messageExpiryMinutes'"));
        await using (var runtime = EngineRuntime.Start(db, topics, TimeProvider.System, new(() => 1, () => 5), readExpiryMinutes: MessageExpiryPreferences.ReadMinutes))
        { Assert.Equal(2701000, db.QueryFirst("SELECT expires_ms FROM engine_message WHERE id='reserved-message'", r => r.Long(0), default)); }
        db.Writer.Write("test.change", s => s.Execute("UPDATE preferences SET value='1' WHERE key='messageExpiryMinutes'"));
        await using var reopened = EngineRuntime.Start(db, topics, TimeProvider.System, new(() => 1, () => 5), readExpiryMinutes: MessageExpiryPreferences.ReadMinutes);
        Assert.Equal(2701000, db.QueryFirst("SELECT expires_ms FROM engine_message WHERE id='reserved-message'", r => r.Long(0), default));
    }

    [Fact]
    public async Task Runtime_backfill_failure_rolls_back_schema_and_retries_with_new_current_snapshot()
    {
        using var file = new TempDatabase(); var topics = new Topics(); using var db = AppDatabase.Open(file.Path, TimeProvider.System, topics); RowStore.Open(db); _ = new PreferenceStore(db);
        db.Writer.Write("test.frozen", s => s.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", "old-v5.sql"))));
        db.Writer.Write("test.fail-upgrade", s => s.Execute("""
            INSERT INTO preferences VALUES('messageExpiryMinutes','45');
            CREATE TRIGGER fail_upgrade BEFORE UPDATE OF schema_version ON engine_meta WHEN NEW.schema_version=6 BEGIN SELECT RAISE(ABORT,'upgrade blocked'); END;
            """));
        var factory = false;
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => EngineRuntime.Start(db, topics, TimeProvider.System, new(() => 1, () => 5), effectsFactory: _ => { factory = true; throw new InvalidOperationException(); }, readExpiryMinutes: MessageExpiryPreferences.ReadMinutes));
        Assert.False(factory); Assert.Equal(5, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default));
        Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('engine_message') WHERE name='expires_ms'", r => r.Long(0), default));
        Assert.Equal(1, db.QueryFirst("SELECT in_flight FROM engine_message WHERE id='reserved-message'", r => r.Long(0), default));
        db.Writer.Write("test.retry", s => s.Execute("DROP TRIGGER fail_upgrade; UPDATE preferences SET value='60' WHERE key='messageExpiryMinutes'"));
        await using var runtime = EngineRuntime.Start(db, topics, TimeProvider.System, new(() => 1, () => 5), readExpiryMinutes: MessageExpiryPreferences.ReadMinutes);
        Assert.Equal(3601000, db.QueryFirst("SELECT expires_ms FROM engine_message WHERE id='reserved-message'", r => r.Long(0), default));
    }

    private static CommandContext Context(EngineFixture f, string id) => new(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), id, 0, 0, 1000);
}
