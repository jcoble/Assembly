using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Infrastructure.Persistence;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class SessionInitialConfigTests
{
    [Fact]
    public void Initial_raw_choices_target_and_direct_folder_survive_two_reopens()
    {
        using var temp = new TempDatabase();
        using var db = TestDatabase.Open(temp.Path);
        var store = SessionStore.Open(db, TimeProvider.System, "/managed");
        var choices = new Dictionary<string, string> { ["model/\"模型"] = "b", ["effort"] = "medium", ["mode"] = "full" };
        var created = store.Create("claude", "repo", new InitialSessionConfig("target", choices), "/actual/cwd");
        foreach (var _ in new[] { 1, 2 })
        {
            var reopened = SessionStore.Open(db, TimeProvider.System, "/managed");
            Assert.Equal(choices, reopened.Config(created.SessionId).Choices);
            Assert.Equal("target", reopened.Config(created.SessionId).ConfigTarget);
            Assert.Null(reopened.Get(created.SessionId)!.AcpSessionId);
            Assert.Equal("/actual/cwd", reopened.Root(reopened.Get(created.SessionId)!));
        }
    }

    [Fact]
    public void Failed_create_validation_has_no_session_or_choices()
    {
        using var temp = new TempDatabase();
        using var db = TestDatabase.Open(temp.Path);
        var store = SessionStore.Open(db, TimeProvider.System, "/managed");
        Assert.Throws<InvalidOperationException>(() => store.Create("claude", "repo", new InitialSessionConfig("target", new Dictionary<string, string> { ["model"] = "b" }),
            validate: () => throw new InvalidOperationException("replaced")));
        Assert.Empty(store.List(null, 10).Sessions);
    }

    [Fact]
    public void A_failure_after_inserting_session_rolls_back_creation_and_initial_choices_together()
    {
        using var temp = new TempDatabase();
        using var db = TestDatabase.Open(temp.Path);
        var store = SessionStore.Open(db, TimeProvider.System, "/managed");
        using (var sql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            sql.Open(); using var command = sql.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_initial BEFORE INSERT ON session_config BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.Create("claude", "repo", new InitialSessionConfig("target", new Dictionary<string, string> { ["model"] = "b" })));
        Assert.Empty(store.List(null, 10).Sessions);
    }

    [Fact]
    public void A_failure_after_native_id_and_config_writes_rolls_back_the_entire_observation()
    {
        using var temp = new TempDatabase();
        using var db = TestDatabase.Open(temp.Path);
        var store = SessionStore.Open(db, TimeProvider.System, "/managed");
        var original = new Dictionary<string, string> { ["model"] = "b", ["effort"] = "medium" };
        var session = store.Create("claude", "repo", new InitialSessionConfig("target", original));
        using (var sql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            sql.Open(); using var command = sql.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_observation BEFORE INSERT ON session_commands BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.ObserveConfig(session, "new-native", "[]", "[]",
            new Dictionary<string, string> { ["model"] = "a" }, () => { }, CancellationToken.None));
        Assert.Null(store.Get(session.SessionId)!.AcpSessionId);
        Assert.Null(store.Config(session.SessionId).Options);
        Assert.Equal(original, store.Config(session.SessionId).Choices);
    }

    [Fact]
    public async Task Partial_initial_refusal_keeps_entire_intent_and_null_native_id()
    {
        using var temp = new TempDatabase();
        using var db = TestDatabase.Open(temp.Path);
        var store = SessionStore.Open(db, TimeProvider.System, "/managed");
        var launcher = new FakeLauncher();
        var target = launcher.ConfigTargets[0].TargetId!;
        var choices = new Dictionary<string, string> { ["model"] = "a", ["effort"] = "high" };
        var session = store.Create("claude", "repo", new InitialSessionConfig(target, choices), FakeLauncher.Checkout);
        await using var pool = new SharedAdapter(launcher, "claude", TimeProvider.System, TextWriter.Null);
        var opener = new SessionOpener(store, launcher, _ => pool);
        var opening = opener.OpenAsync(session.SessionId, "hi", new StageTimes(TimeProvider.System), CancellationToken.None);
        var agent = await launcher.NextAsync();
        var options = SessionWarmConfigTests.Options.TrimEnd(']') + "," + """{"id":"effort","type":"select","currentValue":"low","options":[{"value":"low"},{"value":"high"}]}]""";
        await agent.StartAsync(configOptions: options);
        var model = await agent.ReadAsync("session/set_config_option");
        var changed = options.Replace("\"currentValue\":\"b\"", "\"currentValue\":\"a\"");
        await agent.ReplyAsync(model, $$"""{"configOptions":{{changed}}}""");
        var effort = await agent.ReadAsync("session/set_config_option");
        await agent.ReplyErrorAsync(effort, -32602, "refused effort");
        await agent.CloseAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(agent.Disposed);
        Assert.Equal(choices, store.Config(session.SessionId).Choices);
        Assert.Null(store.Config(session.SessionId).Options);
        Assert.Null(store.Get(session.SessionId)!.AcpSessionId);
    }
}
