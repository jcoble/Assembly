using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mcp;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Providers;

namespace Assembly.Tests.Engine.Auth;

public sealed class AuthNeedControlTests
{
    private const string Need = "sign-in/s/1";

    [Fact]
    public void Prepared_owner_starts_one_exact_target_attempt_and_replays_without_closing_or_notifying_the_need()
    {
        using var f = Fixture(); Seed(f);
        var prepared = Owner(f, "sign_in"); var result = ApplyOwner(f, prepared);
        Assert.True(result.Applied, result.Error); AssertOpen(f, 2);
        Assert.Equal(1, f.Core.Count("engine_sign_in")); Assert.Equal("codex", f.Core.Text("SELECT provider FROM engine_sign_in"));
        Assert.Equal("", f.Core.Text("SELECT account_context FROM engine_sign_in")); Assert.Equal(Need, f.Core.Text("SELECT origin_need_id FROM engine_sign_in"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='begin_sign_in' AND session_id IS NULL AND order_key='provider/codex/'"));
        Assert.Equal(result, ApplyOwner(f, prepared)); Assert.Equal(1, f.Core.Count("engine_sign_in")); AssertOpen(f, 2);
        Assert.Equal(0, f.Core.Count("engine_message")); Assert.Equal(0, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='answer'"));
    }

    [Fact]
    public void Prepared_owner_joins_the_existing_target_attempt_while_leaving_the_need_open()
    {
        using var f = Fixture(); Seed(f);
        Assert.True(f.Apply(new StartSignIn("codex", "", null, null), "existing-operation").Applied);
        var result = ApplyOwner(f, Owner(f, "sign_in"));
        Assert.True(result.Applied, result.Error); AssertOpen(f, 2);
        Assert.Equal(1, f.Core.Count("engine_sign_in")); Assert.Equal(1, f.Core.Count("engine_outbox"));
        Assert.Equal("starting", f.Core.State("engine_sign_in", "existing-operation")); Assert.Equal(0, f.Core.Count("engine_message"));
    }

    [Fact]
    public void Prepared_owner_running_conflict_changes_no_need_attempt_or_message()
    {
        using var f = Fixture(); Seed(f); f.SeedProvider(state: "signed_in"); f.Core.SeedSession("running", "running");
        var result = ApplyOwner(f, Owner(f, "sign_in"));
        Assert.False(result.Applied); Assert.Equal("conflict_running:1", result.Error); AssertOpen(f, 1);
        Assert.Equal(0, f.Core.Count("engine_sign_in")); Assert.Equal(0, f.Core.Count("engine_outbox")); Assert.Equal(0, f.Core.Count("engine_message"));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("unavailable")]
    public void Auth_confirmation_cannot_be_answered_with_an_ordinary_arbitrary_choice(string choice)
    {
        using var f = Fixture(); Seed(f);
        var result = ApplyOwner(f, Owner(f, choice));
        Assert.False(result.Applied); Assert.Equal("bad_args", result.Error); AssertOpen(f, 1);
        Assert.Equal(0, f.Core.Count("engine_sign_in")); Assert.Equal(0, f.Core.Count("engine_message"));
    }

    [Fact]
    public void A_provider_fact_cannot_act_as_the_owner_of_an_auth_confirmation()
    {
        using var f = Fixture(); Seed(f);
        var result = f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement("sign_in"), "manual_answered"), Need, 1, new(ActorKind.System, "adapter"));
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error); AssertOpen(f, 1);
        Assert.Equal(0, f.Core.Count("engine_sign_in")); Assert.Equal(0, f.Core.Count("engine_outbox"));
    }

    [Fact]
    public void A_raw_parent_actor_cannot_replace_the_checked_parent_bridge()
    {
        using var f = Fixture(); Seed(f, parent: true);
        var result = f.Apply(new ResolveNeed("answered", JsonSerializer.SerializeToElement("sign_in")), Need, 1, new(ActorKind.Session, "parent"));
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error); AssertOpen(f, 1);
        Assert.Equal(0, f.Core.Count("engine_sign_in"));
    }

    [Fact]
    public void The_exact_addressed_parent_starts_the_child_target_without_actor_impersonation_or_a_note()
    {
        using var f = Fixture(); Seed(f, parent: true);
        var prepared = Parent(f); var result = ApplyParent(f, prepared);
        Assert.True(result.Applied, result.Error); AssertOpen(f, 2);
        Assert.Equal(1, f.Core.Count("engine_sign_in")); Assert.Equal(Need, f.Core.Text("SELECT origin_need_id FROM engine_sign_in"));
        Assert.Equal(1, f.Core.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='begin_sign_in' AND session_id IS NULL"));
        Assert.Equal(0, f.Core.Count("engine_message")); Assert.Equal(result, ApplyParent(f, prepared));
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("address")]
    public void A_prepared_parent_control_rechecks_its_original_authority_in_the_supplied_writer(string change)
    {
        using var f = Fixture(); Seed(f, parent: true); var prepared = Parent(f);
        f.Core.Db.Writer.Write("test.changed-auth-parent", s => s.Execute(change == "epoch"
            ? "UPDATE engine_session SET epoch=4 WHERE id='parent'"
            : "UPDATE engine_needs_me SET addressee_session_id=NULL WHERE id='sign-in/s/1'"));
        var result = ApplyParent(f, prepared);
        Assert.False(result.Applied); Assert.Equal("forbidden", result.Error); AssertOpen(f, 1);
        Assert.Equal(0, f.Core.Count("engine_sign_in")); Assert.Equal(0, f.Core.Count("engine_outbox")); Assert.Equal(0, f.Core.Count("engine_message"));
    }

    private static AuthRecordFixture Fixture() => new((provider, account) => provider == "codex" && account == "");
    private static void AssertOpen(AuthRecordFixture f, long version)
    { Assert.Equal("open", f.Core.State("engine_needs_me", Need)); Assert.Equal(version, f.Version("engine_needs_me", Need)); }
    private static PreparedNeedCommand Owner(AuthRecordFixture f, string choice) =>
        EngineCommands.PrepareOwnerNeedCommand(Guid.NewGuid(), f.Core.Machine, Need, 1, 0,
            new("answered", JsonSerializer.SerializeToElement(choice)), null, null, default);
    private static CommandResult ApplyOwner(AuthRecordFixture f, PreparedNeedCommand prepared) =>
        f.Core.Db.Writer.Write("test.auth-owner-control", s => f.Commands.ApplyPreparedOwnerNeed(s, prepared, 1000));
    private static PreparedParentNeed Parent(AuthRecordFixture f) => EngineCommands.PrepareParentNeed(Guid.NewGuid(),
        new(Need, ParentNeedAction.Answer, JsonSerializer.SerializeToElement("sign_in")),
        new(Need, 1, "s", f.Core.Machine, 0, "local", null, "confirmation", false, "parent", f.Core.Machine, "parent", null, null), null, default);
    private static CommandResult ApplyParent(AuthRecordFixture f, PreparedParentNeed prepared) =>
        f.Core.Db.Writer.Write("test.auth-parent-control", s => f.Commands.ApplyLocalParentNeed(s, new McpCaller("parent", 3), prepared, 1000));
    private static void Seed(AuthRecordFixture f, bool parent = false)
    {
        if (parent) f.Core.SeedSession("parent", "running", epoch: 3);
        f.Core.SeedSession("s", "needs_me", parentId: parent ? "parent" : null);
        f.Core.Db.Writer.Write("test.auth-control-need", s => s.Execute("""
            INSERT INTO engine_needs_me(id,version,machine,created_ms,updated_ms,session_id,kind,text,options_json,state,addressee_session_id)
            VALUES($id,1,$machine,1000,1000,'s','confirmation',$text,'["sign_in","dismiss"]','open',$parent)
            """, new SqlArg("$id", Need), new("$machine", f.Core.Machine),
            new("$text", "codex needs you to sign in on " + f.Core.Machine + "."), new("$parent", parent ? "parent" : null)));
    }
}
