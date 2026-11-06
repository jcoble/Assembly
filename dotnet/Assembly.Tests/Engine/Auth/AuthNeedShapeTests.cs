using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Providers;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthNeedShapeTests
{
    [Theory]
    [InlineData("sequence")]
    [InlineData("trailing")]
    [InlineData("text")]
    [InlineData("machine-copy")]
    [InlineData("options")]
    [InlineData("closed")]
    [InlineData("provider-request")]
    [InlineData("chat-row")]
    public void A_matching_prefix_is_not_authority_to_start_from_a_different_confirmation(string change)
    {
        using var f = new AuthRecordFixture((provider, account) => provider == "codex" && account == "");
        var id = Seed(f, "s", change);
        var result = f.Apply(new StartSignIn("codex", "", null, id), "operation");
        Assert.False(result.Applied); Assert.Equal("bad_args", result.Error);
        Assert.Equal(0, f.Core.Count("engine_sign_in")); Assert.Equal(0, f.Core.Count("engine_provider"));
        Assert.Equal(0, f.Core.Count("engine_outbox"));
        Assert.Equal(change == "closed" ? "answered" : "open", f.Text("engine_needs_me", id, "state"));
    }

    [Theory]
    [InlineData("s")]
    [InlineData("s%_branch/child")]
    public void The_full_real_target_confirmation_accepts_literal_session_ids(string session)
    {
        using var f = new AuthRecordFixture((provider, account) => provider == "codex" && account == "");
        var id = Seed(f, session, null);
        var result = f.Apply(new StartSignIn("codex", "", null, id), "operation");
        Assert.True(result.Applied, result.Error);
        Assert.Equal(id, f.Text("engine_sign_in", "operation", "origin_need_id"));
        Assert.Equal("open", f.Text("engine_needs_me", id, "state"));
        Assert.Equal(1, f.Core.Count("engine_outbox"));
    }

    private static string Seed(AuthRecordFixture f, string session, string? change)
    {
        f.Core.SeedSession(session, "needs_me", provider: "codex");
        var suffix = change == "sequence" ? "not-a-number" : change == "trailing" ? "1/trailing" : "1";
        var id = "sign-in/" + session + "/" + suffix;
        var text = change == "text" ? "An unrelated confirmation"
            : "codex needs you to sign in on " + (change == "machine-copy" ? "another-machine" : f.Core.Machine) + ".";
        var options = JsonSerializer.Serialize(change == "options" ? new[] { "dismiss", "sign_in" } : new[] { "sign_in", "dismiss" });
        f.Core.Db.Writer.Write("test.auth-confirmation-shape", s => s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state,provider_request_id,chat_row_id)
            VALUES($id,1,$machine,1000,1000,$session,'confirmation',$text,$options,$state,$request,$chat)
            """, new SqlArg("$id", id), new("$machine", f.Core.Machine), new("$session", session), new("$text", text),
            new("$options", options), new("$state", change == "closed" ? "answered" : "open"),
            new("$request", change == "provider-request" ? "actual-provider-request" : null),
            new("$chat", change == "chat-row" ? "actual-ask-row" : null)));
        return id;
    }
}
