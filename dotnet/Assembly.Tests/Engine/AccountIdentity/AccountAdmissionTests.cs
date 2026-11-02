using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;
using Assembly.Engine.Sessions;

namespace Assembly.Tests.Engine.AccountIdentity;

public sealed class AccountAdmissionTests
{
    [Theory]
    [InlineData("check", false)]
    [InlineData("check", true)]
    [InlineData("sign_in", false)]
    [InlineData("sign_in", true)]
    [InlineData("session", false)]
    [InlineData("session", true)]
    [InlineData("import", false)]
    [InlineData("import", true)]
    public void New_metadata_rejects_noncanonical_accounts_without_operational_writes(string boundary, bool uppercase)
    {
        using var f = new EngineFixture(); var account = uppercase ? "acct-v1:" + new string('A', 64) : AccountIdentityFixture.RawCanary;
        var commands = new EngineCommands(f.Machine, f.Limits, accountContext: _ => account, isConfiguredProvider: (p, a) => p == AccountIdentityFixture.Provider && a == account);
        if (boundary == "import")
        {
            var error = Assert.Throws<InvalidOperationException>(() => f.Db.Writer.Write("test.invalid-import", s => SessionImport.Import(s, f.Machine, 1000, [new("s", null, AccountIdentityFixture.Provider, null, "/test", "Session", account)])));
            Assert.Equal("invalid_account_context", error.Message);
        }
        else
        {
            EngineCommand command = boundary switch { "check" => new CheckProvider(), "sign_in" => new StartSignIn(AccountIdentityFixture.Provider, account, null, null), _ => new StartSession(AccountIdentityFixture.Provider, null, "/test", "Session", null, null, null) };
            var id = boundary == "check" ? AccountIdentityFixture.Provider + "/" + account : "operation";
            var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), id, 0, 0, 1000);
            var result = f.Db.Writer.Write("test.invalid-account", s => commands.Apply(s, context, command));
            Assert.False(result.Applied); Assert.Equal(boundary == "check" ? "unavailable" : "bad_args", result.Error);
        }
        foreach (var table in new[] { "engine_session", "engine_provider", "engine_sign_in", "engine_outbox", "engine_event" }) Assert.Equal(0, f.Count(table));
    }

    [Theory]
    [InlineData("check")]
    [InlineData("sign_in")]
    [InlineData("session")]
    [InlineData("import")]
    public void Opaque_accounts_and_literal_slash_provider_are_admitted_unchanged(string boundary)
    {
        using var f = new EngineFixture(); var account = AccountIdentityFixture.Opaque;
        var commands = new EngineCommands(f.Machine, f.Limits, accountContext: _ => account, isConfiguredProvider: (p, a) => p == AccountIdentityFixture.Provider && a == account);
        if (boundary == "import") Assert.Equal(1, f.Db.Writer.Write("test.opaque-import", s => SessionImport.Import(s, f.Machine, 1000, [new("s", null, AccountIdentityFixture.Provider, null, "/test", "Session", account)])));
        else
        {
            EngineCommand command = boundary switch { "check" => new CheckProvider(), "sign_in" => new StartSignIn(AccountIdentityFixture.Provider, account, null, null), _ => new StartSession(AccountIdentityFixture.Provider, null, "/test", "Session", null, null, null) };
            var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), boundary == "check" ? AccountIdentityFixture.Provider + "/" + account : "operation", 0, 0, 1000);
            var result = f.Db.Writer.Write("test.opaque-account", s => commands.Apply(s, context, command)); Assert.True(result.Applied, result.Error);
        }
        var table = boundary is "session" or "import" ? "engine_session" : boundary == "check" ? "engine_provider" : "engine_sign_in";
        Assert.Equal(account, f.Text($"SELECT account_context FROM {table}"));
    }

    [Fact]
    public void Prior_rejection_receipt_precedes_current_account_validation_and_changed_payload_conflicts()
    {
        using var f = new EngineFixture(); EngineCommand command = new StartSignIn(AccountIdentityFixture.Provider, AccountIdentityFixture.RawCanary, null, null);
        var context = new CommandContext(Guid.NewGuid(), f.Machine, new(ActorKind.Window, "owner"), "operation", 0, 0, 1000);
        var prior = new CommandResult(false, false, "unavailable", 0, 0, 0);
        // Produce the retained rejection through the real journal, with the original command fingerprint.
        Assert.Equal(prior, f.Db.Writer.Write("test.prior-rejection", s => EngineJournal.Execute(s, context, JsonSerializer.Serialize(command, EngineJson.Default.EngineCommand), () => prior)));
        var receipt = f.Text("SELECT fingerprint||result_json FROM engine_command_receipt");
        var commands = new EngineCommands(f.Machine, f.Limits, isConfiguredProvider: (_, _) => true);
        Assert.Equal(prior, f.Db.Writer.Write("test.replay-rejection", s => commands.Apply(s, context, command)));
        var changed = f.Db.Writer.Write("test.changed-account", s => commands.Apply(s, context, new StartSignIn(AccountIdentityFixture.Provider, AccountIdentityFixture.Opaque, null, null)));
        Assert.Equal("command_id_conflict", changed.Error); Assert.Equal(receipt, f.Text("SELECT fingerprint||result_json FROM engine_command_receipt"));
        Assert.Equal(0, f.Count("engine_provider")); Assert.Equal(0, f.Count("engine_sign_in")); Assert.Equal(0, f.Count("engine_outbox")); Assert.Equal(0, f.Count("engine_event"));
    }
}
