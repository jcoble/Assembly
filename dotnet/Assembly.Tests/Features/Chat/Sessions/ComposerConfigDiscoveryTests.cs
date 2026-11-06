using Assembly.Application.Features.Chat.Sessions.Configuration;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class ComposerConfigDiscoveryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static string Target(FakeLauncher launcher) => ConfigTargetIdentity.Capture(launcher, "claude").TargetId;
    private static string Options(string model, string effort) => $$"""
        [{"id":"raw/model","category":"model","type":"select","currentValue":"{{model}}","custom":{"label":"模型"},"options":[{"value":"a","name":"A"},{"value":"b","name":"B"}]},
         {"id":"effort","category":"thought_level","type":"select","currentValue":"{{effort}}","options":[{"value":"{{(model == "a" ? "low" : "minimal")}}","name":"lower"},{"value":"{{(model == "a" ? "high" : "medium")}}","name":"upper"}]}]
        """.ReplaceLineEndings("");

    [Fact]
    public async Task Private_ACP2_discovery_preserves_raw_options_and_real_per_model_defaults_then_stops()
    {
        var folder = Directory.CreateTempSubdirectory("assembly-composer-cwd-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "notes.md"), "owner draft");
            var launcher = new FakeLauncher();
            var discovering = new ComposerConfigDiscovery(launcher, TimeProvider.System).DiscoverAsync("claude", Target(launcher), folder, default);
            var link = await launcher.NextAsync();
            var initialize = await link.ReadAsync("initialize");
            Assert.Equal(2, initialize.GetProperty("params").GetProperty("protocolVersion").GetInt32());
            Assert.False(initialize.GetProperty("params").TryGetProperty("air", out _));
            await link.ReplyAsync(initialize, "{\"protocolVersion\":2}");
            var opening = await link.OpenAsync(configOptions: Options("a", "high"));
            Assert.Equal(folder, opening.GetProperty("params").GetProperty("cwd").GetString());
            Assert.Empty(opening.GetProperty("params").GetProperty("mcpServers").EnumerateArray());
            var set = await link.ReadAsync("session/set_config_option");
            Assert.Equal("raw/model", set.GetProperty("params").GetProperty("configId").GetString());
            Assert.Equal("b", set.GetProperty("params").GetProperty("value").GetString());
            await link.ReplyAsync(set, $$"""{"configOptions":{{Options("b", "minimal")}}}""");
            var result = await discovering.WaitAsync(Wait);
            using var initial = JsonDocument.Parse(result.Config.Options!);
            using var alternate = JsonDocument.Parse(result.ModelOptions["b"]);
            Assert.Equal("模型", initial.RootElement[0].GetProperty("custom").GetProperty("label").GetString());
            Assert.Equal("high", initial.RootElement[1].GetProperty("currentValue").GetString());
            Assert.Equal("minimal", alternate.RootElement[1].GetProperty("currentValue").GetString());
            Assert.Null(result.Config.Commands);
            Assert.Empty(result.Config.Choices);

            Assert.True(link.Disposed);
            Assert.True(link.Exited.IsCompleted);
            Assert.Equal("owner draft", await File.ReadAllTextAsync(Path.Combine(folder, "notes.md")));
            Assert.Equal(["notes.md"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Target_replaced_before_ingress_launches_nothing()
    {
        var launcher = new FakeLauncher();
        var target = Target(launcher);
        launcher.Agents = [launcher.Agents[0] with { Args = ["changed"] }];
        await Assert.ThrowsAsync<CallFailed>(() => new ComposerConfigDiscovery(launcher, TimeProvider.System).DiscoverAsync("claude", target, "/tmp", default));
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task Configuration_replaced_while_metadata_is_held_refuses_the_result_and_stops_the_exact_link()
    {
        var launcher = new FakeLauncher();
        var task = new ComposerConfigDiscovery(launcher, TimeProvider.System).DiscoverAsync("claude", Target(launcher), "/tmp", default);
        var link = await launcher.NextAsync();
        await link.ReplyAsync(await link.ReadAsync("initialize"), "{\"protocolVersion\":2}");
        var opening = await link.ReadAsync("session/new");
        launcher.Agents = [launcher.Agents[0] with { Args = ["changed"] }];
        await link.ReplyAsync(opening, "{\"sessionId\":\"private\",\"configOptions\":[]}");
        await Assert.ThrowsAsync<CallFailed>(() => task.WaitAsync(Wait));
        Assert.Equal(1, launcher.Launches);
        Assert.True(link.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancel_and_whole_probe_deadline_join_the_exact_private_link(bool deadline)
    {
        var launcher = new FakeLauncher();
        var time = new FakeTimeProvider();
        using var stop = new CancellationTokenSource();
        var task = new ComposerConfigDiscovery(launcher, time).DiscoverAsync("claude", Target(launcher), "/tmp", stop.Token);
        var link = await launcher.NextAsync();
        await link.ReadAsync("initialize");
        if (deadline) time.Advance(ComposerConfigDiscovery.Limit); else await stop.CancelAsync();
        if (deadline) await Assert.ThrowsAsync<CallFailed>(() => task.WaitAsync(Wait));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Wait));
        Assert.True(link.Disposed);
        Assert.True(link.Exited.IsCompleted);
    }

    [Fact]
    public async Task Ordered_command_metadata_is_preserved_as_raw_JSON_without_private_chat_rows()
    {
        var folder = Directory.CreateTempSubdirectory("assembly-composer-cwd-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "notes.md"), "owner draft");
            var launcher = new FakeLauncher();
            var task = new ComposerConfigDiscovery(launcher, TimeProvider.System).DiscoverAsync("claude", Target(launcher), folder, default);
            var link = await launcher.NextAsync();
            await link.ReplyAsync(await link.ReadAsync("initialize"), "{\"protocolVersion\":2}");
            var opening = await link.ReadAsync("session/new");
            Assert.Equal(folder, opening.GetProperty("params").GetProperty("cwd").GetString());
            await link.UpdateAsync("{\"sessionUpdate\":\"available_commands_update\",\"availableCommands\":[{\"name\":\"actual\",\"description\":\"命令\",\"custom\":{\"x\":1}}]}", "private");
            await link.ReplyAsync(opening, "{\"sessionId\":\"private\",\"configOptions\":[]}");
            var result = await task.WaitAsync(Wait);
            using var commands = JsonDocument.Parse(result.Config.Commands!);
            Assert.Equal("actual", commands.RootElement[0].GetProperty("name").GetString());
            Assert.Equal(1, commands.RootElement[0].GetProperty("custom").GetProperty("x").GetInt32());
            var wire = ComposerConfigJson.Json(result.Config, result.ModelOptions);
            Assert.Equal(JsonValueKind.Array, wire.GetProperty("commands").ValueKind);
            Assert.Equal(JsonValueKind.Array, wire.GetProperty("options").ValueKind);
            Assert.True(link.Disposed);
            Assert.Equal("owner draft", await File.ReadAllTextAsync(Path.Combine(folder, "notes.md")));
            Assert.Equal(["notes.md"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task A_refused_alternative_model_has_no_invented_options_or_effort()
    {
        var launcher = new FakeLauncher();
        var task = new ComposerConfigDiscovery(launcher, TimeProvider.System).DiscoverAsync("claude", Target(launcher), "/tmp", default);
        var link = await launcher.NextAsync();
        await link.StartAsync(configOptions: Options("a", "high"));
        await link.ReplyErrorAsync(await link.ReadAsync("session/set_config_option"), -32603, "RAW_PROVIDER_CANARY");
        var result = await task.WaitAsync(Wait);
        Assert.Equal(["a"], result.ModelOptions.Keys);
        Assert.DoesNotContain("RAW_PROVIDER_CANARY", ComposerConfigJson.Json(result.Config, result.ModelOptions).GetRawText());
        Assert.True(link.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_auth_required_at_initialize_or_open_is_fixed_and_does_not_start_login(bool atOpen)
    {
        var launcher = new FakeLauncher();
        var task = new ComposerConfigDiscovery(launcher, TimeProvider.System).DiscoverAsync("claude", Target(launcher), "/tmp", default);
        var link = await launcher.NextAsync();
        var request = await link.ReadAsync("initialize");
        if (atOpen)
        {
            await link.ReplyAsync(request, "{\"protocolVersion\":2}");
            request = await link.ReadAsync("session/new");
        }
        await link.ReplyErrorAsync(request, -32000, "RAW_AUTH_CANARY");
        var failure = await Assert.ThrowsAsync<CallFailed>(() => task.WaitAsync(Wait));
        Assert.Equal("authentication_required", failure.Reason);
        Assert.DoesNotContain("RAW_AUTH_CANARY", failure.Message);
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task Cancelling_a_late_owned_launch_waits_and_disposes_it_before_any_initialize()
    {
        var launcher = new FakeLauncher();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = new FakeLink();
        launcher.Launch = async _ => { entered.TrySetResult(); await released.Task; return link; };
        using var stop = new CancellationTokenSource();
        var task = new ComposerConfigDiscovery(launcher, TimeProvider.System).DiscoverAsync("claude", Target(launcher), "/tmp", stop.Token);
        await entered.Task.WaitAsync(Wait);
        await stop.CancelAsync();
        Assert.False(task.IsCompleted);
        released.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Wait));
        Assert.True(link.Disposed);
        Assert.True(link.Exited.IsCompleted);
    }
}
