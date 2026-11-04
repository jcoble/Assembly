using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Desktop.Serve;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Infrastructure.Persistence;
using Assembly.Infrastructure.Remote;
using Assembly.Tests.Infrastructure.Http;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Fact]
    public async Task Composer_transport_discovery_crosses_pinned_socket_to_owner_without_rows_or_desktop_launch()
    {
        var agents = await FeatureResultAsync(_desktop!, DesktopToken, "machines", "agents", new { machine = Box });
        var target = agents.GetProperty("agents")[0].GetProperty("targetId").GetString()!;
        using var socket = await OpenSocketAsync(_desktop!);
        await SendTextAsync(socket, $$$$$"""{"t":"call","id":1,"feature":"config","method":"discover","args":{"agent":"claude","targetId":"{{{{{target}}}}}","cwd":"/tmp","machine":"{{{{{Box}}}}}"}}""");
        var agent = await _boxLauncher.NextAsync();
        var opened = await agent.StartAsync(configOptions: SessionWarmConfigTests.Options);
        Assert.Equal("/tmp", opened.GetProperty("params").GetProperty("cwd").GetString());
        var set = await agent.ReadAsync("session/set_config_option");
        var changed = SessionWarmConfigTests.Options.Replace("\"currentValue\":\"b\"", "\"currentValue\":\"a\"");
        await agent.ReplyAsync(set, $$$$$"""{"configOptions":{{{{{changed}}}}}}""");
        var frame = JsonDocument.Parse(await ReadTextAsync(socket)).RootElement;
        Assert.Equal("result", frame.GetProperty("t").GetString());
        Assert.Equal(JsonValueKind.Array, frame.GetProperty("result").GetProperty("modelOptions").GetProperty("a").ValueKind);
        Assert.True(agent.Disposed);
        Assert.Equal(0, _desktopLauncher.Launches);
        Assert.Empty((await FeatureResultAsync(_box, BoxToken, "sessions", "page", new { limit = 200 })).GetProperty("sessions").EnumerateArray());
        Assert.Empty((await FeatureResultAsync(_desktop!, DesktopToken, "sessions", "page", new { limit = 200 })).GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task Composer_transport_create_saves_initial_choices_and_cwd_then_owner_warms_and_resumes_same_id()
    {
        var agents = await FeatureResultAsync(_desktop!, DesktopToken, "machines", "agents", new { machine = Box });
        var target = agents.GetProperty("agents")[0].GetProperty("targetId").GetString()!;
        var project = await FeatureResultAsync(_desktop!, DesktopToken, "projects", "add", new { machine = Box, path = _boxHome });
        var projectId = project.GetProperty("project").GetProperty("id").GetString()!;
        using var socket = await OpenSocketAsync(_desktop!);
        await SendTextAsync(socket, $$$$$"""{"t":"call","id":1,"feature":"sessions","method":"create","args":{"agent":"claude","clientId":"composer-create","machine":"{{{{{Box}}}}}","projectId":"{{{{{projectId}}}}}","initialConfig":{"targetId":"{{{{{target}}}}}","choices":{"model":"b"}}}}""");
        var created = JsonDocument.Parse(await ReadTextAsync(socket)).RootElement;
        var id = created.GetProperty("result").GetProperty("sessionId").GetString()!;
        var saved = await FeatureResultAsync(_box, BoxToken, "config", "get", new { session = id });
        Assert.Equal("b", saved.GetProperty("choices").GetProperty("model").GetString());
        Assert.Equal(_boxHome, (await SessionAsync(_box, BoxToken, id)).GetProperty("root").GetString());
        await SendTextAsync(socket, $$$$$"""{"t":"call","id":2,"feature":"config","method":"warm","args":{"session":"{{{{{id}}}}}","targetId":"{{{{{target}}}}}"}}""");
        var warmingTask = _boxLauncher.NextAsync();
        var warmReply = ReadTextAsync(socket);
        if (await Task.WhenAny(warmingTask, warmReply) == warmReply) Assert.Fail(await warmReply);
        var warming = await warmingTask;
        var open = await warming.StartAsync(acpSessionId: "owner-native", configOptions: SessionWarmConfigTests.Options);
        Assert.Equal("session/new", open.GetProperty("method").GetString());
        var warmed = JsonDocument.Parse(await warmReply).RootElement;
        Assert.Equal("result", warmed.GetProperty("t").GetString());
        Assert.True(warming.Disposed);
        Assert.Equal("owner-native", (await SessionAsync(_box, BoxToken, id)).GetProperty("acpSessionId").GetString());
        using var sendingSocket = await OpenSocketAsync(_desktop!);
        var sending = ApiTests.QueueSocketTurnAsync(sendingSocket, id);
        var running = await _boxLauncher.NextAsync();
        var resumed = await running.StartAsync(acpSessionId: "owner-native", configOptions: SessionWarmConfigTests.Options);
        Assert.Equal("session/resume", resumed.GetProperty("method").GetString());
        Assert.Equal(_boxHome, resumed.GetProperty("params").GetProperty("cwd").GetString());
        var prompt = await running.ReadAsync("session/prompt");
        await running.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        await running.CloseAsync();
        Assert.Equal(0, _desktopLauncher.Launches);
    }

    [Fact]
    public async Task Composer_transport_stale_advertised_owner_target_is_refused_before_launch()
    {
        var agents = await FeatureResultAsync(_desktop!, DesktopToken, "machines", "agents", new { machine = Box });
        var target = agents.GetProperty("agents")[0].GetProperty("targetId").GetString()!;
        _boxLauncher.Agents = [_boxLauncher.Agents[0] with { Air = ["replacement-air"] }];
        using var socket = await OpenSocketAsync(_desktop!);
        await SendTextAsync(socket, $$$$$"""{"t":"call","id":1,"feature":"config","method":"discover","args":{"agent":"claude","targetId":"{{{{{target}}}}}","cwd":"/tmp","machine":"{{{{{Box}}}}}"}}""");
        var frame = JsonDocument.Parse(await ReadTextAsync(socket)).RootElement;
        Assert.Equal("error", frame.GetProperty("t").GetString());
        Assert.Contains("changed", frame.GetProperty("message").GetString());
        Assert.Equal(0, _boxLauncher.Launches);
        Assert.Equal(0, _desktopLauncher.Launches);
    }
    [Fact]
    public async Task Composer_transport_choices_survive_both_restarts_and_apply_before_one_prompt()
    {
        var agents = await FeatureResultAsync(_desktop!, DesktopToken, "machines", "agents", new { machine = Box });
        var target = agents.GetProperty("agents")[0].GetProperty("targetId").GetString()!;
        var project = await FeatureResultAsync(_desktop!, DesktopToken, "projects", "add", new { machine = Box, path = _boxHome });
        var projectId = project.GetProperty("project").GetProperty("id").GetString()!;
        var created = await FeatureResultAsync(_desktop!, DesktopToken, "sessions", "create", new
        {
            agent = "claude",
            clientId = "composer-restart",
            machine = Box,
            projectId,
            initialConfig = new { targetId = target, choices = new Dictionary<string, string> { ["model"] = "b", ["effort"] = "medium", ["mode"] = "full" } }
        });
        var id = created.GetProperty("sessionId").GetString()!;
        Assert.Equal(0, _boxLauncher.Launches);
        await _desktop!.DisposeAsync(); _desktop = null;
        await _box.DisposeAsync();
        _box = await DesktopBackend.StartAsync(_boxHome, BoxToken, 0, _boxLauncher, _log);
        await StartDesktopAsync();
        var before = await FeatureResultAsync(_box, BoxToken, "config", "get", new { session = id });
        Assert.Equal("medium", before.GetProperty("choices").GetProperty("effort").GetString());
        using var sendingSocket = await OpenSocketAsync(_desktop!);
        var sending = ApiTests.QueueSocketTurnAsync(sendingSocket, id);
        var agent = await _boxLauncher.NextAsync();
        string Options(string model, string effort, string mode) => SessionWarmConfigTests.Options.Replace("\"currentValue\":\"b\"", $"\"currentValue\":\"{model}\"").TrimEnd(']')
            + "," + JsonSerializer.Serialize(new { id = "effort", type = "select", currentValue = effort, options = model == "a" ? new[] { new { value = "low" }, new { value = "high" } } : new[] { new { value = "minimal" }, new { value = "medium" } } })
            + "," + JsonSerializer.Serialize(new { id = "mode", type = "select", currentValue = mode, options = new[] { new { value = "ask" }, new { value = "full" } } }) + "]";
        await agent.StartAsync(acpSessionId: "after-restart", configOptions: Options("a", "high", "ask"));
        foreach (var (configId, value, model, effort, mode) in new[] { ("model", "b", "b", "minimal", "ask"), ("effort", "medium", "b", "medium", "ask"), ("mode", "full", "b", "medium", "full") })
        {
            var set = await agent.ReadAsync("session/set_config_option");
            Assert.Equal(configId, set.GetProperty("params").GetProperty("configId").GetString());
            Assert.Equal(value, set.GetProperty("params").GetProperty("value").GetString());
            Assert.Equal(JsonValueKind.Null, (await SessionAsync(_box, BoxToken, id)).GetProperty("acpSessionId").ValueKind);
            await agent.ReplyAsync(set, "{\"configOptions\":" + Options(model, effort, mode) + "}");
        }
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal("hi", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal("after-restart", (await SessionAsync(_box, BoxToken, id)).GetProperty("acpSessionId").GetString());
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        await agent.CloseAsync();
        var rows = await RowsAsync(_box, BoxToken, id);
        Assert.Single(rows, row => row.GetProperty("kind").GetString() == "user");
        Assert.Equal(0, _desktopLauncher.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composer_transport_replaced_connection_profile_refuses_old_wrapper_before_forwarding(bool identicalProfile)
    {
        using var temp = new TempDatabase();
        using var db = TestDatabase.Open(temp.Path);
        using var hub = new LiveHub(RowStore.Open(db));
        var store = SessionStore.Open(db, TimeProvider.System, "/tmp");
        var status = new MachineStatus(new global::Assembly.Infrastructure.Topics.Topics(), [Box]);
        await using var old = new RemoteMachine(Box, _box.Address, BoxToken, hub, store, _log, status, connectionIdentity: "ssh-profile-A");
        await old.ProbeAsync(default);
        await old.ReadMirrorOwnerAsync(default);
        var advertised = (await old.AgentsAsync(CancellationToken.None))[0].TargetId!;
        await using var replacement = new RemoteMachine(Box, _box.Address, BoxToken, hub, store, _log, status, connectionIdentity: identicalProfile ? "ssh-profile-A" : "ssh-profile-B");
        await Assert.ThrowsAsync<CallFailed>(() => replacement.DiscoverConfigAsync("claude", advertised, "/tmp", CancellationToken.None));
        Assert.Equal(0, _boxLauncher.Launches);
        await replacement.ProbeAsync(default);
        await replacement.ReadMirrorOwnerAsync(default);
        var current = (await replacement.AgentsAsync(CancellationToken.None))[0].TargetId!;
        Assert.NotEqual(advertised, current);
        var discovering = replacement.DiscoverConfigAsync("claude", current, "/tmp", CancellationToken.None);
        var agent = await _boxLauncher.NextAsync();
        await agent.StartAsync();
        await discovering.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(agent.Disposed);
        Assert.Empty((await FeatureResultAsync(_box, BoxToken, "sessions", "page", new { limit = 200 })).GetProperty("sessions").EnumerateArray());
    }

}
