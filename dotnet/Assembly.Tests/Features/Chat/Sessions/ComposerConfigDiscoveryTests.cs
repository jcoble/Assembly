using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class ComposerConfigDiscoveryTests
{
    private static string Options(string model, string effort) => $$"""
        [{"id":"raw/model","category":"model","type":"select","currentValue":"{{model}}","custom":{"label":"模型"},"options":[{"value":"a","name":"A"},{"value":"b","name":"B"}]},
         {"id":"effort","category":"thought_level","type":"select","currentValue":"{{effort}}","options":[{"value":"{{(model == "a" ? "low" : "minimal")}}","name":"lower"},{"value":"{{(model == "a" ? "high" : "medium")}}","name":"upper"}]}]
        """.ReplaceLineEndings("");

    [Fact]
    public async Task Discovery_uses_private_v1_session_and_retains_actual_model_defaults_then_stops()
    {
        var launcher = new FakeLauncher();
        var target = launcher.ConfigTargets.Single(x => x.Id == "claude").TargetId;
        var discovering = new ComposerConfigDiscovery(launcher, TimeProvider.System).DiscoverAsync("claude", target!, "/tmp", CancellationToken.None);
        var agent = await launcher.NextAsync();
        var opened = await agent.StartAsync(configOptions: Options("a", "high"));
        Assert.Equal("/tmp", opened.GetProperty("params").GetProperty("cwd").GetString());
        Assert.Empty(opened.GetProperty("params").GetProperty("mcpServers").EnumerateArray());
        var set = await agent.ReadAsync("session/set_config_option");
        Assert.Equal("b", set.GetProperty("params").GetProperty("value").GetString());
        await agent.ReplyAsync(set, $$"""{"configOptions":{{Options("b", "minimal")}}}""");
        var result = await discovering.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("模型", JsonDocument.Parse(result.Config.Options!).RootElement[0].GetProperty("custom").GetProperty("label").GetString());
        Assert.Equal("minimal", JsonDocument.Parse(result.ModelOptions["b"]).RootElement[1].GetProperty("currentValue").GetString());
        Assert.Contains("medium", result.ModelOptions["b"]);
        Assert.True(agent.Disposed);
        Assert.True(agent.Exited.IsCompleted);
        Assert.Null(result.Config.Commands);
    }

    [Fact]
    public async Task A_target_replaced_before_ingress_launches_nothing()
    {
        var launcher = new FakeLauncher();
        var old = launcher.ConfigTargets[0].TargetId;
        launcher.Agents = [launcher.Agents[0] with { Args = ["changed"] }];
        await Assert.ThrowsAsync<CallFailed>(() => new ComposerConfigDiscovery(launcher, TimeProvider.System)
            .DiscoverAsync("claude", old!, "/tmp", CancellationToken.None));
        Assert.Equal(0, launcher.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_and_whole_probe_deadline_stop_the_exact_private_link(bool deadline)
    {
        var launcher = new FakeLauncher();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var cancel = new CancellationTokenSource();
        var task = new ComposerConfigDiscovery(launcher, clock).DiscoverAsync("claude", launcher.ConfigTargets[0].TargetId!, "/tmp", cancel.Token);
        var link = await launcher.NextAsync();
        await link.ReadAsync("initialize");
        if (deadline) clock.Advance(ComposerConfigDiscovery.Limit); else cancel.Cancel();
        if (deadline) await Assert.ThrowsAsync<CallFailed>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(link.Disposed);
        Assert.True(link.Exited.IsCompleted);
    }

    [Fact]
    public void Target_encoding_distinguishes_boundaries_order_air_and_environment()
    {
        var descriptor = new AgentDescriptor("a", "Name", "cmd", ["ab", "c"], null, ["air"]);
        var env = new Dictionary<string, string> { ["HOME"] = "/same" };
        var identity = ConfigTargetIdentity.Of(descriptor, env);
        Assert.NotEqual(identity, ConfigTargetIdentity.Of(descriptor with { Args = ["a", "bc"] }, env));
        Assert.NotEqual(identity, ConfigTargetIdentity.Of(descriptor with { Air = ["different"] }, env));
        Assert.NotEqual(identity, ConfigTargetIdentity.Of(descriptor, new Dictionary<string, string> { ["HOME"] = "/other" }));
        Assert.Equal(identity, ConfigTargetIdentity.Of(descriptor with { Name = "renamed" }, env));
    }
}
