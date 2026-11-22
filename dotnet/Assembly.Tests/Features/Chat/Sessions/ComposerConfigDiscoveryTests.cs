using Assembly.Application.Features.Chat.Sessions.Configuration;
using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Microsoft.Extensions.Time.Testing;
using Assembly.Domain.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class ComposerConfigDiscoveryTests
{
    [Fact]
    public async Task Settings_discovery_reports_a_missing_adapter_before_starting_its_program()
    {
        var home = Directory.CreateTempSubdirectory("assembly-missing-adapter-").FullName;
        try
        {
            var descriptor = new AgentDescriptor("claude", "Claude Code", "/usr/bin/true",
                [Path.Combine(home, "node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js")], null);
            var launcher = new Assembly.Infrastructure.Agents.AdapterLauncher([descriptor], home, new UnclaimedPermissions());
            var target = ConfigTargetIdentity.Capture(launcher, "claude").TargetId;
            var error = await Assert.ThrowsAsync<CallFailed>(() => new ComposerConfigDiscovery(launcher, TimeProvider.System)
                .DiscoverAsync("claude", target, home, default));
            Assert.Equal("adapter_not_installed", error.Reason);
        }
        finally { Directory.Delete(home, true); }
    }

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
    public async Task The_same_discovery_asked_again_starts_no_second_adapter_for_its_answer()
    {
        var launcher = new FakeLauncher();
        await using var discovery = new ComposerConfigDiscovery(launcher, TimeProvider.System);
        var first = discovery.DiscoverAsync("claude", Target(launcher), "/tmp", default);
        var second = discovery.DiscoverAsync("claude", Target(launcher), "/tmp", default);
        var link = await launcher.NextAsync();
        Assert.Equal(1, launcher.Launches);
        await link.StartAsync(configOptions: Options("a", "high"));
        await link.ReplyAsync(await link.ReadAsync("session/set_config_option"), $$"""{"configOptions":{{Options("b", "minimal")}}}""");
        var one = await first.WaitAsync(Wait);
        var two = await second.WaitAsync(Wait);
        Assert.Equal(one.Config.Options, two.Config.Options);
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public async Task A_reopened_discovery_returns_the_saved_catalogue_before_the_refresh_answers()
    {
        using var file = new TempDatabase();
        var launcher = new FakeLauncher();
        var target = Target(launcher);
        ComposerCatalogue first;
        using (var database = TestDatabase.Open(file.Path))
        {
            var store = SessionStore.Open(database, TimeProvider.System, "/tmp");
            await using var discovery = new ComposerConfigDiscovery(launcher, TimeProvider.System, store);
            var pending = discovery.DiscoverAsync("claude", target, "/tmp", default);
            var link = await launcher.NextAsync();
            await link.StartAsync(configOptions: Options("a", "high"));
            await link.ReplyAsync(await link.ReadAsync("session/set_config_option"), $$"""{"configOptions":{{Options("b", "minimal")}}}""");
            first = await pending.WaitAsync(Wait);
        }

        launcher = new FakeLauncher();
        using var reopened = TestDatabase.Open(file.Path);
        var reopenedStore = SessionStore.Open(reopened, TimeProvider.System, "/tmp");
        await using (var discovery = new ComposerConfigDiscovery(launcher, TimeProvider.System, reopenedStore))
        {
            var saved = await discovery.DiscoverAsync("claude", target, "/tmp", default).WaitAsync(Wait);
            Assert.Equal(first.Config.Options, saved.Config.Options);
            Assert.Equal(first.ModelOptions["b"], saved.ModelOptions["b"]);
            var refresh = await launcher.NextAsync();
            await refresh.ReadAsync("initialize");
        }
    }

    [Fact]
    public async Task A_second_warm_draft_starts_no_adapter_after_discovery_in_this_run()
    {
        using var file = new TempDatabase();
        using var database = TestDatabase.Open(file.Path);
        var store = SessionStore.Open(database, TimeProvider.System, "/tmp");
        var launcher = new FakeLauncher();
        var target = Target(launcher);
        await using var discovery = new ComposerConfigDiscovery(launcher, TimeProvider.System, store);
        var cold = discovery.DiscoverAsync("claude", target, "/tmp", default);
        var link = await launcher.NextAsync();
        await link.StartAsync(configOptions: "[]");
        await cold.WaitAsync(Wait);

        // Run any scheduled launch up to its first I/O so the count is independent of task scheduling.
        var outer = SynchronizationContext.Current;
        Task<ComposerCatalogue> first;
        Task<ComposerCatalogue> second;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new InlineLaunchContext());
            first = discovery.DiscoverAsync("claude", target, "/tmp", default);
            second = discovery.DiscoverAsync("claude", target, "/tmp", default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(outer); }

        await Task.WhenAll(first, second).WaitAsync(Wait);
        Assert.Equal(1, launcher.Launches);
    }

    private sealed class InlineLaunchContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            var outer = Current;
            // Only the first continuation runs inline. Adapter I/O completes on its own scheduler.
            SetSynchronizationContext(null);
            try { callback(state); }
            finally { SetSynchronizationContext(outer); }
        }
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
