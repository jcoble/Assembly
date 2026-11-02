using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.NeedsMe;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthNeedDismissTests
{
    private const string Need = "sign-in/s/1";

    [Fact]
    public void Explicit_window_dismiss_closes_only_auth_and_fails_its_unreserved_messages_without_an_ordinary_note()
    {
        using var f = Fixture(); Seed(f);
        Assert.True(f.Apply(new ResolveNeed("dismissed", null), Need, 1).Applied);
        AssertDismissed(f);
    }

    [Fact]
    public void Prepared_owner_dismiss_option_has_the_same_closed_terminal_and_no_provider_answer()
    {
        using var f = Fixture(); Seed(f); var prepared = Owner(f);
        var result = Apply(f, prepared); Assert.True(result.Applied, result.Error); AssertDismissed(f);
        Assert.Equal(result, Apply(f, prepared)); AssertDismissed(f);
    }

    [Fact]
    public void The_exact_addressed_parent_dismisses_without_a_second_parent_or_answer_message()
    {
        using var f = Fixture(); Seed(f, parent: true);
        var prepared = EngineCommands.PrepareParentNeed(Guid.NewGuid(), new(Need, ParentNeedAction.Answer, JsonSerializer.SerializeToElement("dismiss")),
            new(Need, 1, "s", f.Core.Machine, 0, "local", null, "confirmation", false, "parent", f.Core.Machine, "parent", null, null), null, default);
        var result = f.Core.Db.Writer.Write("test.auth-parent-dismiss", s => f.Commands.ApplyLocalParentNeed(s, new McpCaller("parent", 3), prepared, 1000));
        Assert.True(result.Applied, result.Error); AssertDismissed(f);
    }

    [Fact]
    public void A_System_fact_cannot_borrow_the_window_dismiss_lifecycle()
    {
        using var f = Fixture(); Seed(f);
        var result = f.Apply(new ResolveNeed("dismissed", null, "manual_answered"), Need, 1, new(ActorKind.System, "adapter"));
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error);
        Assert.Equal("open", f.Core.State("engine_needs_me", Need)); Assert.Equal("queued", f.Core.State("engine_message", "queued"));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("uncertain")]
    [InlineData("unavailable")]
    public void Dismiss_never_promotes_a_retained_send_reservation_to_known_unsent(string state)
    {
        using var f = Fixture(); Seed(f);
        f.Core.Db.Writer.Write("test.retained-auth-reservation", s => s.Execute("""
            INSERT INTO engine_outbox(id,version,machine,created_ms,updated_ms,event_seq,session_id,kind,payload_json,state,attempt,order_key)
            VALUES('send/queued/1',0,$machine,1000,1000,0,'s','send','{"messageId":"queued"}',$state,1,'s')
            """, new SqlArg("$machine", f.Core.Machine), new("$state", state)));
        Assert.True(f.Apply(new ResolveNeed("dismissed", null), Need, 1).Applied);
        Assert.Equal("queued", f.Core.State("engine_message", "queued"));
        Assert.Equal("auth_required", f.Core.Text("SELECT reason FROM engine_message WHERE id='queued'"));
        Assert.Equal(state, f.Core.State("engine_outbox", "send/queued/1"));
    }

    [Fact]
    public void Dismiss_message_failure_rolls_back_the_need_receipt_events_and_full_original_payload_then_exact_input_retries()
    {
        using var f = Fixture(); Seed(f); var prepared = Owner(f);
        f.Core.Db.Writer.Write("test.auth-dismiss-fault", s => s.Execute("CREATE TRIGGER auth_dismiss_fault BEFORE UPDATE OF state ON engine_message WHEN NEW.state='failed' BEGIN SELECT RAISE(ABORT,'dismiss fault'); END"));
        Assert.Throws<SqliteException>(() => Apply(f, prepared));
        Assert.Equal("open", f.Core.State("engine_needs_me", Need)); Assert.Equal(1, f.Version("engine_needs_me", Need));
        Assert.Equal(0, f.Core.Count("engine_command_receipt")); Assert.Equal(0, f.Core.Count("engine_event"));
        Assert.Equal("queued", f.Core.State("engine_message", "queued"));
        f.Core.Db.Writer.Write("test.auth-dismiss-unfault", s => s.Execute("DROP TRIGGER auth_dismiss_fault"));
        Assert.True(Apply(f, prepared).Applied); AssertDismissed(f);
    }

    private static AuthRecordFixture Fixture() => new((provider, account) => provider == "codex" && account == "");
    private static PreparedNeedCommand Owner(AuthRecordFixture f) => EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Core.Machine,
        Need, 1, 0, new("answered", JsonSerializer.SerializeToElement("dismiss")), null, null, default);
    private static CommandResult Apply(AuthRecordFixture f, PreparedNeedCommand prepared) => f.Core.Db.Writer.Write("test.auth-owner-dismiss", s => f.Commands.ApplyPreparedOwnerNeed(s, prepared, 1000));
    private static void AssertDismissed(AuthRecordFixture f)
    {
        Assert.Equal("dismissed", f.Core.State("engine_needs_me", Need)); Assert.Null(f.Core.Text("SELECT answer_json FROM engine_needs_me WHERE id=$id", new SqlArg("$id", Need)));
        Assert.Equal("open", f.Core.State("engine_needs_me", "ordinary-need"));
        foreach (var id in new[] { "queued", "held" })
        {
            Assert.Equal("failed", f.Core.State("engine_message", id)); Assert.Equal("sign_in_dismissed", f.Core.Text("SELECT reason FROM engine_message WHERE id=$id", new SqlArg("$id", id)));
            Assert.Equal(Encoding.UTF8.GetBytes("body\0" + id), f.Core.Db.QueryFirst("SELECT CAST(text AS BLOB) FROM engine_message WHERE id=$id", r => r.Blob(0), default, new SqlArg("$id", id)));
            Assert.Equal("[\"b\",\"a\"]", f.Core.Text("SELECT attachment_ids_json FROM engine_message WHERE id=$id", new SqlArg("$id", id)));
        }
        Assert.Equal("queued", f.Core.State("engine_message", "ordinary")); Assert.Equal("queued", f.Core.State("engine_message", "elsewhere"));
        Assert.Equal(4, f.Core.Count("engine_message")); Assert.Equal(0, f.Core.Count("engine_outbox")); Assert.Equal(0, f.Core.Count("engine_sign_in"));
    }
    private static void Seed(AuthRecordFixture f, bool parent = false)
    {
        if (parent) f.Core.SeedSession("parent", "running", epoch: 3);
        f.Core.SeedSession("s", "needs_me", parentId: parent ? "parent" : null); f.Core.SeedSession("else");
        f.Core.Db.Writer.Write("test.auth-dismiss-seed", s =>
        {
            s.Execute("""
                INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state,addressee_session_id)
                VALUES($need,1,$machine,1000,1000,'s','confirmation',$text,'["sign_in","dismiss"]','open',$parent),
                    ('ordinary-need',1,$machine,1000,1000,'s','question','Ordinary question','[]','open',NULL)
                """, new SqlArg("$need", Need), new("$machine", f.Core.Machine), new("$text", "codex needs you to sign in on " + f.Core.Machine + "."), new("$parent", parent ? "parent" : null));
            foreach (var id in new[] { "queued", "held", "ordinary", "elsewhere" }) s.Execute("""
                INSERT INTO engine_message(id,version,machine,created_ms,updated_ms,sender_kind,to_session_id,text,mode,state,reason,expires_ms,
                    framed,held_epoch,sender_label,client_id,attachment_ids_json)
                VALUES($id,1,$machine,1000,1000,'window',$session,CAST($body AS TEXT),'prompt',$state,$reason,10000,0,0,'Captured',$id,'["b","a"]')
                """, new SqlArg("$id", id), new("$machine", f.Core.Machine), new("$session", id == "elsewhere" ? "else" : "s"),
                new("$body", Encoding.UTF8.GetBytes("body\0" + id)), new("$state", id == "held" ? "held" : "queued"), new("$reason", id == "ordinary" ? null : "auth_required"));
            return 0;
        });
    }
}
