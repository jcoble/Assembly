using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine;

public sealed class SessionImportTests
{
    [Fact]
    public void Trusted_import_preserves_seven_fields_parent_order_and_no_start_effect()
    {
        using var f = new EngineFixture();
        ImportedSession[] source = [new("child", "root", "retired-custom", null, "/managed/root", "Child", ""),
            new("root", null, "configured-work", "model-wire", "/managed/root", "", "/account-home")];
        Assert.Equal(2, f.Db.Writer.Write("test.import", scope => SessionImport.Import(scope, f.Machine, 1000, source)));
        Assert.Equal("root", f.Text("SELECT parent_id FROM engine_session WHERE id='child'"));
        Assert.Null(f.Text("SELECT model FROM engine_session WHERE id='child'"));
        Assert.Equal("retired-custom", f.Text("SELECT provider FROM engine_session WHERE id='child'"));
        Assert.Equal("model-wire", f.Text("SELECT model FROM engine_session WHERE id='root'"));
        Assert.Equal("/account-home", f.Text("SELECT account_context FROM engine_session WHERE id='root'"));
        Assert.Equal("", f.Text("SELECT title FROM engine_session WHERE id='root'"));
        Assert.Equal("idle", f.State("engine_session", "child"));
        Assert.Equal(2, f.Number("SELECT COUNT(*) FROM engine_session WHERE managed_checkout=1"));
        Assert.Equal(new[] { "root", "child" }, f.Events().Select(e => e.EntityId));
        Assert.All(f.Events(), e => Assert.Equal("session.started", e.Kind));
        Assert.Equal(0, f.Count("engine_outbox")); Assert.Equal(0, f.Count("engine_message")); Assert.Equal(0, f.Count("engine_command_receipt"));
        var before = f.Events();
        Assert.Equal(0, f.Db.Writer.Write("test.reimport", scope => SessionImport.Import(scope, f.Machine, 2000, source)));
        Assert.Equal(before, f.Events()); Assert.Equal(2, f.Count("engine_session"));
    }

    [Fact]
    public void Missing_parent_and_foreign_identity_roll_back_without_root_promotion()
    {
        using var f = new EngineFixture();
        var source = new[] { new ImportedSession("root", null, "custom", null, "/managed", "", ""),
            new ImportedSession("child", "missing-peer", "custom", null, "/managed", "Child", "") };
        Assert.Throws<InvalidOperationException>(() => f.Db.Writer.Write("test.import", s => SessionImport.Import(s, f.Machine, 1000, source)));
        Assert.Equal(0, f.Count("engine_session")); Assert.Empty(f.Events());
        f.SeedSession("root");
        f.Db.Writer.Write("test.foreign", s => s.Execute("UPDATE engine_session SET machine='peer' WHERE id='root'"));
        Assert.Throws<InvalidOperationException>(() => f.Db.Writer.Write("test.import", s => SessionImport.Import(s, f.Machine, 1000, source[..1])));
        Assert.Equal("peer", f.Text("SELECT machine FROM engine_session WHERE id='root'")); Assert.Empty(f.Events());
    }

    [Fact]
    public void Historical_import_bypasses_creation_caps_but_new_start_does_not()
    {
        using var f = new EngineFixture(new(() => 1, () => 1));
        ImportedSession[] source = [new("root", null, "custom", null, "/managed", "Root", ""),
            new("a", "root", "custom", null, "/managed", "A", ""), new("b", "a", "custom", null, "/managed", "B", "")];
        Assert.Equal(3, f.Db.Writer.Write("test.import", s => SessionImport.Import(s, f.Machine, 1000, source)));
        Assert.Equal("depth_limit", f.Apply(new StartSession("custom", "wire", "/managed", "New", "a", null, null), "new", actor: new(ActorKind.Session, "a")).Error);
        Assert.Equal(3, f.Count("engine_session"));
    }

    [Theory]
    [InlineData("codex", "CODEX_HOME")]
    [InlineData("claude", "CLAUDE_CONFIG_DIR")]
    public void Explicit_vendor_home_obeys_launch_environment_precedence(string family, string variable)
    {
        var agent = new AgentDescriptor("work-profile", "Custom name", "unrelated-command", [], null, VendorFamily: family);
        Assert.Equal("/inherited", AccountContext.Of(agent, key => key == variable ? "/inherited" : null));
        Assert.Equal("/explicit", AccountContext.Of(agent with { Env = new Dictionary<string, string> { [variable] = "/explicit" } }, _ => "/inherited"));
        Assert.Equal("", AccountContext.Of(agent with { Env = new Dictionary<string, string> { [variable] = "" } }, _ => "/inherited"));
        Assert.Equal("", AccountContext.Of(agent, _ => null));
        Assert.Equal("", AccountContext.Of(agent with { Id = "codex", Name = "Claude", VendorFamily = null }, _ => "/must-not-infer"));
    }

    [Fact]
    public void Raw_model_selector_is_separate_from_existing_display_name()
    {
        using var options = JsonDocument.Parse("""[{"id":"model-opt","category":"model","type":"select","currentValue":"model-wire-1","options":[{"value":"model-wire-1","name":"Friendly Model"}]}]""");
        Assert.Equal("Friendly Model", ConfigOptions.ModelName(options.RootElement));
        Assert.Equal("model-wire-1", ConfigOptions.ModelValue(options.RootElement));
        using var absent = JsonDocument.Parse("[]"); Assert.Null(ConfigOptions.ModelValue(absent.RootElement));
        using var blank = JsonDocument.Parse("""[{"category":"model","currentValue":" "}]"""); Assert.Null(ConfigOptions.ModelValue(blank.RootElement));
    }
}
