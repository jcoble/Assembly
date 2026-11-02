using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;
using Assembly.Infrastructure.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Engine.AccountIdentity;

public sealed class AccountSchemaTests
{
    [Fact]
    public void Fresh_legacy_chat_and_existing_C3_configuration_remain_in_place_when_Engine_starts_at_nine()
    {
        using var file = new TempDatabase();
        using (var connection = new SqliteConnection("Data Source=" + file.Path))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", "row-format4.sql")); command.ExecuteNonQuery();
        }
        using var db = TestDatabase.Open(file.Path); var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        _ = SessionStore.Open(db, time, "checkouts");
        db.Writer.Write("test.legacy-c3-configuration", scope =>
        {
            if (scope.QueryFirst("SELECT COUNT(*) FROM pragma_table_info('session_config') WHERE name='config_target'", r => r.Long(0)) == 0)
                scope.Execute("ALTER TABLE session_config ADD COLUMN config_target TEXT NULL");
            for (var i = 0; i < 3; i++) scope.Execute("INSERT INTO session_config(session_id,choices,config_target) VALUES($id,'{}',$target)", new SqlArg("$id", "legacy-" + i), new("$target", "existing-c3-target-" + i));
            return 0;
        });
        var before = LegacyDigest(db); Assert.Equal(0, db.QueryFirst("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='engine_meta'", r => r.Long(0), default));
        EngineSchema.Open(db, 1001);
        Assert.Equal(9, db.QueryFirst("SELECT schema_version FROM engine_meta", r => r.Long(0), default)); Assert.Equal(before, LegacyDigest(db));
        Assert.Equal(4, db.QueryFirst("PRAGMA user_version", r => r.Long(0), default));
        foreach (var table in new[] { "engine_session", "engine_provider", "engine_sign_in", "engine_outbox", "engine_event" }) Assert.Equal(0, db.QueryFirst($"SELECT COUNT(*) FROM {table}", r => r.Long(0), default));
    }

    [Fact]
    public void Compatible_default_context_history_changes_only_schema_authority_and_keeps_exact_references_and_receipts()
    {
        using var f = new AccountIdentityFixture(); var before = f.Digest(); var result = f.Initialize();
        Assert.Equal(8, result.PreviousVersion); Assert.Equal(9, f.Core.Number("SELECT schema_version FROM engine_meta")); Assert.Equal(before, f.Digest());
        Assert.Equal(9, f.Initialize().PreviousVersion); Assert.Equal(before, f.Digest());
    }

    [Theory]
    [InlineData("session", false)]
    [InlineData("provider", false)]
    [InlineData("sign_in", false)]
    [InlineData("session", true)]
    [InlineData("provider", true)]
    [InlineData("sign_in", true)]
    public void Legacy_nonempty_history_has_no_opaque_provenance_even_when_its_text_looks_like_a_digest(string entity, bool digestLooking)
    {
        using var f = new AccountIdentityFixture(); f.Context(entity, digestLooking ? AccountIdentityFixture.Opaque : AccountIdentityFixture.RawCanary);
        var before = f.Digest(); var error = Assert.Throws<InvalidOperationException>(() => f.Initialize());
        Assert.Equal("account_identity_upgrade_required", error.Message); Assert.Equal(8, f.Core.Number("SELECT schema_version FROM engine_meta")); Assert.Equal(before, f.Digest());
    }

    [Theory]
    [InlineData("session")]
    [InlineData("provider")]
    [InlineData("sign_in")]
    public void Nine_revalidates_context_invariants_before_any_recovery_or_new_journal_write(string entity)
    {
        using var f = new AccountIdentityFixture(); f.Version(9); f.Context(entity, AccountIdentityFixture.RawCanary);
        var before = f.Digest(); var error = Assert.Throws<InvalidOperationException>(() => f.Initialize());
        Assert.Equal("account_identity_upgrade_required", error.Message); Assert.Equal(9, f.Core.Number("SELECT schema_version FROM engine_meta")); Assert.Equal(before, f.Digest());
    }

    [Fact]
    public void Nine_keeps_a_valid_opaque_account_and_literal_slash_provider_identity_byte_for_byte()
    {
        using var f = new AccountIdentityFixture(seed: false); f.Core.SeedSession("s", provider: AccountIdentityFixture.Provider);
        f.Context("session", AccountIdentityFixture.Opaque); f.Version(9); var before = f.Digest();
        Assert.Equal(9, f.Initialize().PreviousVersion); Assert.Equal(before, f.Digest());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compatible_default_active_auth_is_interrupted_once_after_nine_and_never_relaunches(bool signIn)
    {
        using var f = new AccountIdentityFixture(seed: false);
        var configured = new Func<string, string, bool>((provider, account) => provider == AccountIdentityFixture.Provider && account == "");
        var commands = new EngineCommands(f.Core.Machine, f.Core.Limits, isConfiguredProvider: configured);
        var id = signIn ? "active-operation" : AccountIdentityFixture.Target;
        var context = new CommandContext(Guid.NewGuid(), f.Core.Machine, new(ActorKind.Window, "owner"), id, 0, 0, 1000);
        var applied = f.Core.Db.Writer.Write("test.active-default", scope => commands.Apply(scope, context,
            signIn ? new StartSignIn(AccountIdentityFixture.Provider, "", null, null) : new CheckProvider())); Assert.True(applied.Applied, applied.Error);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var runtime = EngineRuntime.Start(f.Core.Db, f.Topics, f.Core.Time, f.Core.Limits, effectsReady: ready.Task, isConfiguredProvider: configured))
        {
            Assert.Equal(9, f.Core.Number("SELECT schema_version FROM engine_meta"));
            Assert.Equal(signIn ? "failed" : "could_not_check", f.Core.Text(signIn ? "SELECT state FROM engine_sign_in" : "SELECT auth_state FROM engine_provider"));
        }
        var receipts = f.Core.Count("engine_command_receipt");
        await using (var runtime = EngineRuntime.Start(f.Core.Db, f.Topics, f.Core.Time, f.Core.Limits, effectsReady: ready.Task, isConfiguredProvider: configured))
        { Assert.Equal(receipts, f.Core.Count("engine_command_receipt")); Assert.Equal(1, f.Core.Count("engine_outbox")); }
    }

    [Fact]
    public void Future_schema_ten_remains_unsupported_without_mutating_its_authority()
    {
        using var f = new AccountIdentityFixture(); f.Version(10); var before = f.Digest();
        Assert.Throws<InvalidOperationException>(() => f.Initialize()); Assert.Equal(10, f.Core.Number("SELECT schema_version FROM engine_meta")); Assert.Equal(before, f.Digest());
    }

    private static string LegacyDigest(global::Assembly.Infrastructure.Persistence.AppDatabase db)
    {
        var rows = new StringBuilder();
        foreach (var table in new[] { "rows", "sessions", "session_info", "session_config" })
        {
            var columns = db.Query($"PRAGMA table_info({table})", r => r.Text(1), default).OrderBy(name => name, StringComparer.Ordinal);
            foreach (var row in db.Query($"SELECT json_array({string.Join(',', columns.Select(name => '"' + name + '"'))}) AS body FROM {table} ORDER BY body", r => r.Text(0), default)) rows.Append(table).Append(':').Append(row).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rows.ToString())));
    }
}
