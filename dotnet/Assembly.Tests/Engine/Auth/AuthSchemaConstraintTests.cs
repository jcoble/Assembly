using Assembly.Engine.Persistence;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthSchemaConstraintTests
{
    [Fact]
    public void Active_sign_in_is_unique_per_machine_provider_account_but_terminal_attempt_releases_slot()
    {
        using var f = new EngineFixture();
        Add("first", "account-A", "starting");
        Assert.Throws<SqliteException>(() => Add("duplicate", "account-A", "waiting_for_person"));
        Assert.Equal(1, f.Count("engine_sign_in"));
        Add("separate-account", "account-B", "finishing");
        Assert.Equal(2, f.Count("engine_sign_in"));
        f.Db.Writer.Write("test.auth.terminal", s => s.Execute("UPDATE engine_sign_in SET state='signed_in' WHERE id='first'"));
        Add("new-attempt", "account-A", "starting");
        Assert.Equal(3, f.Count("engine_sign_in"));
        void Add(string id, string account, string state) => f.Db.Writer.Write("test.auth.active", s => s.Execute("""
            INSERT INTO engine_sign_in(id,version,machine,created_ms,updated_ms,provider,account_context,state,deadline_ms)
            VALUES($id,1,$machine,1000,1000,'codex',$account,$state,301000)
            """, new("$id", id), new("$machine", f.Machine), new("$account", account), new("$state", state)));
    }

    [Fact]
    public void Auth_first_provider_may_have_no_catalogue_facts_but_attempt_cannot_lack_deadline()
    {
        using var f = new EngineFixture();
        f.Db.Writer.Write("test.auth.no-catalogue", s => s.Execute("""
            INSERT INTO engine_provider(id,version,machine,created_ms,updated_ms,provider,account_context,installation,auth_state)
            VALUES('codex/account-A',1,$machine,1000,1000,'codex','account-A','unknown','needs_sign_in')
            """, new Assembly.Application.Common.Database.SqlArg("$machine", f.Machine)));
        Assert.Null(f.Text("SELECT facts_json FROM engine_provider"));
        Assert.Null(f.Text("SELECT checked_ms FROM engine_provider"));
        Assert.Throws<SqliteException>(() => f.Db.Writer.Write("test.auth.invalid-facts", s => s.Execute("UPDATE engine_provider SET facts_json='invalid json'")));
        Assert.Throws<SqliteException>(() => f.Db.Writer.Write("test.auth.no-deadline", s => s.Execute("""
            INSERT INTO engine_sign_in(id,version,machine,created_ms,updated_ms,provider,account_context,state)
            VALUES('op',1,$machine,1000,1000,'codex','account-A','starting')
            """, new Assembly.Application.Common.Database.SqlArg("$machine", f.Machine))));
        Assert.Equal(0, f.Count("engine_sign_in"));
    }
}
