using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Persistence;
using Assembly.Tests.Engine.AccountIdentity;
using Microsoft.Extensions.Time.Testing;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Engine.Projects;

public sealed class SessionProjectSchemaTests
{
    [Fact]
    public void A_fresh_engine_has_schema_ten_and_unassigned_ready_project_fields()
    {
        using var f = new EngineFixture(); f.SeedSession();
        Assert.Equal(14, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Null(f.Text("SELECT project_id FROM engine_session WHERE id='s'"));
        Assert.Null(f.Text("SELECT branch FROM engine_session WHERE id='s'"));
        Assert.Equal("ready", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
    }
    [Fact]
    public void A_nine_upgrade_preserves_session_identity_and_gains_only_unassigned_project_state()
    {
        using var f = new EngineFixture(predecessor: AccountIdentity.AccountIdentityFixture.LoadEight); f.SeedSession(); RemoveProjectFields(f);
        f.Db.Writer.Write("test.schema-nine", s => s.Execute("UPDATE engine_meta SET schema_version=9"));
        var before = f.Text("SELECT id||'|'||provider||'|'||folder||'|'||account_context FROM engine_session WHERE id='s'");
        var originalMachine = f.Machine;
        Assert.Equal(originalMachine, EngineSchema.Open(f.Db, 2000));
        Assert.Equal(14, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(before, f.Text("SELECT id||'|'||provider||'|'||folder||'|'||account_context FROM engine_session WHERE id='s'"));
        Assert.Null(f.Text("SELECT project_id FROM engine_session WHERE id='s'"));
        Assert.Equal("ready", f.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
    }
    [Fact]
    public void A_fault_advancing_the_version_rolls_back_the_structural_upgrade()
    {
        using var f = new EngineFixture(predecessor: AccountIdentity.AccountIdentityFixture.LoadEight); f.SeedSession(); RemoveProjectFields(f);
        f.Db.Writer.Write("test.reject-project-upgrade", scope => scope.Execute("""
            UPDATE engine_meta SET schema_version=9;
            CREATE TRIGGER reject_project_upgrade BEFORE UPDATE OF schema_version ON engine_meta
            WHEN NEW.schema_version=11 BEGIN SELECT RAISE(ABORT,'injected before version advance'); END;
            """));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => EngineSchema.Open(f.Db, 2000));
        Assert.Equal(9, f.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(0, f.Number("SELECT COUNT(*) FROM pragma_table_info('engine_session') WHERE name='project_id'"));
        Assert.Equal(1, f.Count("engine_session"));
    }
    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void Invalid_retained_account_identity_refuses_before_project_upgrade_and_preserves_version(int version, bool digestLooking)
    {
        using var f = new AccountIdentityFixture(); RemoveProjectFields(f.Core); f.Version(version);
        f.Context("session", digestLooking ? AccountIdentityFixture.Opaque : AccountIdentityFixture.RawCanary);
        var before = f.Digest();
        var error = Assert.Throws<InvalidOperationException>(() => f.Initialize());
        Assert.Equal("account_identity_upgrade_required", error.Message);
        Assert.Equal(version, f.Core.Number("SELECT schema_version FROM engine_meta"));
        Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM pragma_table_info('engine_session') WHERE name='project_id'"));
        Assert.Equal(before, f.Digest());
    }
    [Fact]
    public void An_unbound_session_store_reads_its_legacy_session_with_nullable_project_fields()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var sessions = SessionStore.Open(db, TimeProvider.System, "/isolated");
        var original = sessions.Create("codex", "plain");
        Assert.Equal(4, db.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('session_info') WHERE name IN ('project_id','checkout_root','branch','client_id')", r => r.Long(0), default));
        Assert.Equal(original, sessions.Get(original.SessionId));
        Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM sqlite_master WHERE name='engine_meta'", r => r.Long(0), default));
    }
    [Fact]
    public void Projection_schema_reentry_preserves_existing_config_and_working_folder()
    {
        using var file = new TempDatabase(); using var db = TestDatabase.Open(file.Path);
        var sessions = SessionStore.Open(db, TimeProvider.System, "/isolated");
        var session = sessions.Create("codex", "plain", workingFolder: "/isolated/actual");
        db.Writer.Write("test.projection-schema", scope => { SessionStore.EnsureEngineProjectionSchema(scope); SessionStore.EnsureEngineProjectionSchema(scope); return 0; });
        Assert.Equal("/isolated/actual", sessions.Get(session.SessionId)!.WorkingFolder);
        Assert.Equal(1, db.QueryFirst("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='session_project_once'", r => r.Long(0), default));
    }
    private static void RemoveProjectFields(EngineFixture f) => f.Db.Writer.Write("test.pre-project-schema", scope =>
    {
        scope.Execute("DROP INDEX IF EXISTS engine_session_pending_checkout_folder");
        foreach (var column in new[] { "project_id", "branch", "checkout_state", "checkout_operation_id", "checkout_intent_json" })
            if (scope.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('engine_session') WHERE name=$name", r => r.Long(0), new SqlArg("$name", column)) != 0)
                scope.Execute($"ALTER TABLE engine_session DROP COLUMN {column}");
        return 0;
    });
}
