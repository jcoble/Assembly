using System.Text.Json;
using Assembly.Application.Features.Preferences;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine;

public sealed class SessionLimitPreferencesTests
{
    [Theory]
    [InlineData(null, 1, 5)]
    [InlineData("null", 1, 5)]
    [InlineData("{", 1, 5)]
    [InlineData("{}", 1, 5)]
    [InlineData("\"9\"", 1, 5)]
    [InlineData("true", 1, 5)]
    [InlineData("[]", 1, 5)]
    [InlineData("1.5", 1, 5)]
    [InlineData("1e999", 1, 5)]
    [InlineData("NaN", 1, 5)]
    [InlineData("2147483648", 1, 5)]
    [InlineData("-2147483649", 1, 5)]
    [InlineData("2147483647", 2147483647, 2147483647)]
    [InlineData("-2147483648", -2147483648, -2147483648)]
    [InlineData("5.0", 5, 5)]
    [InlineData("9", 9, 9)]
    [InlineData("0", 0, 0)]
    [InlineData("25", 25, 25)]
    public void Persisted_numbers_preserve_integers_and_default_invalid_values(string? json, int depth, int children)
    {
        Assert.Equal(depth, SessionLimitPreferences.Depth(json));
        Assert.Equal(children, SessionLimitPreferences.Children(json));
    }

    [Theory]
    [InlineData("9", 9, 9)]
    [InlineData("2147483647", 2147483647, 10)]
    [InlineData("0", 1, 1)]
    [InlineData("2147483648", 1, 5)]
    [InlineData("25", 25, 10)]
    [InlineData("invalid", 1, 5)]
    [InlineData("5.0", 5, 5)]
    public void Effective_limits_normalize_saved_values(string json, int depth, int children)
    {
        var limits = new SessionLimits(() => SessionLimitPreferences.Depth(json), () => SessionLimitPreferences.Children(json));
        Assert.Equal(depth, limits.ReadDepth());
        Assert.Equal(children, limits.ReadChildren());
    }

    [Fact]
    public async Task Committed_preference_save_changes_the_next_command_on_the_same_runtime()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession("top");
        f.SeedSession("child", parentId: "top");
        var store = new PreferenceStore(f.Db);
        using var one = JsonDocument.Parse("""{"sessions.depthLimit":1}""");
        store.Save(one.RootElement);
        var limits = new SessionLimits(() => SessionLimitPreferences.Depth(store.Read("sessions.depthLimit")),
            () => SessionLimitPreferences.Children(store.Read("sessions.childLimit")));
        await using var owner = EngineRuntime.Start(f.Db, topics, f.Time, limits);
        IEngineRuntime runtime = owner;
        CommandResult Start(string id) => runtime.Write(scope => runtime.Commands.Apply(scope,
            new(Guid.NewGuid(), runtime.Machine, new(ActorKind.Session, "child"), id, 0, 0, 1000),
            new StartSession("codex", "model", "/test", "Grandchild", "child", null, null)));
        Assert.Equal("depth_limit", Start("refused").Error);
        using var nine = JsonDocument.Parse("""{"sessions.depthLimit":9}""");
        store.Save(nine.RootElement);
        Assert.True(Start("allowed").Applied);
        Assert.Equal("9", store.Read("sessions.depthLimit"));
        Assert.Equal(3, f.Count("engine_session"));
    }

    [Fact]
    public async Task Runtime_forwards_dynamic_message_limit_with_session_limits()
    {
        var topics = new Topics();
        using var f = new EngineFixture(topics: topics);
        f.SeedSession("top");
        f.SeedSession("child", parentId: "top");
        var limit = 1;
        await using var owner = EngineRuntime.Start(f.Db, topics, f.Time, new(() => 9, () => 5), messageLimit: () => limit);
        IEngineRuntime runtime = owner;
        CommandResult Send(string id) => runtime.Write(scope => runtime.Commands.Apply(scope,
            new(Guid.NewGuid(), runtime.Machine, new(ActorKind.Session, "child"), id, 0, 0, 1000),
            new SendMessage("child", "top", "Hello", "prompt")));
        Assert.True(Send("one").Applied);
        Assert.Equal("message_limit", Send("two").Error);
        limit = 2;
        Assert.True(Send("three").Applied);
        Assert.Equal(2, f.Count("engine_message"));
    }
}
