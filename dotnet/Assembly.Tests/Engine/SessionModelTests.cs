using System.Text.Json;
using Assembly.Engine.Persistence;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine;

public sealed class SessionModelTests
{
    [Fact]
    public void Existing_owner_root_can_start_before_its_first_prompt_supplies_a_title()
    {
        using var f = new EngineFixture();
        var result = f.Apply(new StartSession("custom", null, "/folder", "", null, null, null), "root", actor: new(ActorKind.Window, "owner"));
        Assert.True(result.Applied, result.Error);
        Assert.Equal("", f.Text("SELECT title FROM engine_session WHERE id='root'"));
    }

    [Theory]
    [InlineData("", "window", null, null, false)]
    [InlineData("", "owner", "parent", null, false)]
    [InlineData("", "owner", null, "run", false)]
    [InlineData("", "session", null, null, false)]
    [InlineData(" ", "owner", null, null, false)]
    [InlineData(null, "owner", null, null, false)]
    [InlineData("Title", "owner", null, null, true)]
    public void Untitled_allowance_does_not_weaken_other_required_title_guards(string? title, string actor, string? parent, string? run, bool accepted)
    {
        using var f = new EngineFixture();
        var result = f.Apply(new StartSession("custom", null, "/folder", title!, parent, run, null), "root",
            actor: new(actor == "session" ? ActorKind.Session : ActorKind.Window, actor));
        Assert.Equal(accepted, result.Applied);
        if (!accepted) Assert.Equal("bad_args", result.Error);
    }

    [Fact]
    public void Omitted_nullable_location_fields_keep_old_concrete_polymorphic_bytes_and_receipt_replay()
    {
        using var f = new EngineFixture();
        var command = new StartSession("codex", "wire", "/folder", "Title", null, null, null);
        const string concrete = """{"provider":"codex","model":"wire","folder":"/folder","title":"Title","parentId":null,"runId":null,"taskUrl":null}""";
        const string polymorphic = """{"kind":"start_session","provider":"codex","model":"wire","folder":"/folder","title":"Title","parentId":null,"runId":null,"taskUrl":null}""";
        Assert.Equal(concrete, JsonSerializer.Serialize(command, EngineJson.Default.StartSession));
        Assert.Equal(polymorphic, JsonSerializer.Serialize(command, EngineJson.Default.EngineCommand));
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), "root", 0, 0, 1000);
        var original = new CommandResult(true, false, null, 1, 7, 0);
        f.Db.Writer.Write("test.old-receipt", scope => EngineJournal.Execute(scope, context, polymorphic, () => original));
        var before = f.Text("SELECT fingerprint||result_json FROM engine_command_receipt");
        Assert.Equal(original, f.Apply(context, command));
        Assert.Equal(before, f.Text("SELECT fingerprint||result_json FROM engine_command_receipt"));
        Assert.Equal(0, f.Count("engine_session")); Assert.Equal(0, f.Count("engine_outbox")); Assert.Empty(f.Events());
        Assert.Contains("\"managedCheckout\":false", JsonSerializer.Serialize(command with { ManagedCheckout = false }, EngineJson.Default.StartSession));
    }

    [Theory]
    [InlineData(false, "/parent", false)]
    [InlineData(true, "/different", false)]
    [InlineData(true, "/parent", true)]
    public void Session_managed_start_requires_its_local_managed_parent_and_identical_authoritative_folder(bool parentManaged, string folder, bool accepted)
    {
        using var f = new EngineFixture(); f.SeedSession("parent");
        f.Db.Writer.Write("test.parent-policy", scope => scope.Execute("UPDATE engine_session SET folder='/parent',managed_checkout=$managed WHERE id='parent'", new Assembly.Application.Common.Database.SqlArg("$managed", parentManaged ? 1 : 0)));
        var result = f.Apply(new StartSession("codex", null, folder, "Child", "parent", null, null, ManagedCheckout: true), "child", actor: new(ActorKind.Session, "parent"));
        Assert.Equal(accepted, result.Applied);
        if (!accepted) { Assert.Equal("bad_args", result.Error); Assert.Equal(1, f.Count("engine_session")); Assert.Equal(0, f.Count("engine_outbox")); }
        else Assert.Equal(1, f.Number("SELECT managed_checkout FROM engine_session WHERE id='child'"));
    }

    [Fact]
    public void Unknown_model_starts_with_the_actual_provider_default_left_unresolved()
    {
        using var f = new EngineFixture();
        var result = f.Apply(new StartSession("custom-launcher", null, "/folder", "Title", null, null, null), "root");
        Assert.True(result.Applied, result.Error);
        Assert.Null(f.Text("SELECT model FROM engine_session WHERE id='root'"));
        Assert.Equal("custom-launcher", f.Text("SELECT provider FROM engine_session WHERE id='root'"));
        Assert.Equal(1, f.Count("engine_outbox"));
        Assert.Single(f.Events());
    }

    [Fact]
    public void Start_projection_and_account_context_share_the_command_transaction_and_replay_once()
    {
        using var f = new EngineFixture();
        var calls = 0;
        var account = "acct-v1:" + new string('a', 64);
        var fail = true;
        var commands = new EngineCommands(f.Machine, f.Limits, projectStart: (scope, context, start) =>
        {
            calls++;
            Assert.Equal(account, scope.QueryFirst("SELECT account_context FROM engine_session WHERE id=$id", r => r.Text(0), new Assembly.Application.Common.Database.SqlArg("$id", context.EntityId)));
            if (fail) throw new IOException("projection failed");
        }, accountContext: _ => account);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "window"), "root", 0, 0, 1000);
        var command = new StartSession("custom", "wire", "/folder", "Title", null, null, null);
        Assert.Throws<IOException>(() => f.Db.Writer.Write("test.start", scope => commands.Apply(scope, context, command)));
        Assert.Equal(0, f.Count("engine_session")); Assert.Equal(0, f.Count("engine_outbox"));
        Assert.Empty(f.Events()); Assert.Equal(0, f.Count("engine_command_receipt"));
        fail = false;
        Assert.True(f.Db.Writer.Write("test.start", scope => commands.Apply(scope, context, command)).Applied);
        Assert.True(f.Db.Writer.Write("test.replay", scope => commands.Apply(scope, context, command)).Applied);
        Assert.Equal(2, calls); Assert.Single(f.Events()); Assert.Equal(1, f.Count("engine_outbox"));
    }

    [Fact]
    public void Model_observation_is_system_only_fenced_metadata_without_run_recomputation()
    {
        using var f = new EngineFixture(); f.SeedSession(); f.SeedRun();
        Assert.Equal("forbidden", f.Apply(new ReportSessionModel("wire"), "s").Error);
        Assert.Equal("forbidden", f.Apply(new ReportSessionModel("wire"), "s", actor: new(ActorKind.Session, "s")).Error);
        Assert.Equal("version_conflict", f.Apply(new ReportSessionModel("wire"), "s", version: 1, actor: new(ActorKind.System, "system")).Error);
        Assert.Equal("stale_epoch", f.Apply(new ReportSessionModel("wire"), "s", epoch: 1, actor: new(ActorKind.System, "system")).Error);
        Assert.True(f.Apply(new ReportSessionModel("wire"), "s", actor: new(ActorKind.System, "system")).Applied);
        Assert.Equal("wire", f.Text("SELECT model FROM engine_session WHERE id='s'"));
        var e = Assert.Single(f.Events()); Assert.Equal("session.changed", e.Kind);
        Assert.Equal(0, f.Count("engine_outbox")); Assert.Equal(0, f.Number("SELECT version FROM engine_run WHERE id='r'"));
        Assert.True(f.Apply(new ReportSessionModel(null), "s", version: 1, actor: new(ActorKind.System, "system")).Applied);
        Assert.Null(f.Text("SELECT model FROM engine_session WHERE id='s'"));
    }
}
