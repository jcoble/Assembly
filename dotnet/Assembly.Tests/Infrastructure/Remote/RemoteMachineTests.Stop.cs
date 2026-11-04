using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Desktop.Serve;
using Assembly.Tests.Features.Chat.Sessions;
using Assembly.Tests.Infrastructure.Http;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Theory]
    [InlineData(false, "running")]
    [InlineData(true, "requires_action")]
    public async Task Native_child_stop_over_the_registered_socket_cancels_only_advertised_current_work(bool remote, string state)
    {
        var (parent, agent, prompt) = await StartControlParentAsync(remote);
        using var rows = await OpenLiveAsync(_desktop!, DesktopToken, parent, 0);
        await ApiTests.ReadRowsAsync(rows, 2);
        await agent.UpdateAsync($$$$"""{"sessionUpdate":"subagent_update","sessionId":"native-child","state":{"state":"{{{{state}}}}"},"capabilities":{"cancel":{}}}""", "native-parent");
        var child = await ChildRowAsync(rows, "native-child");
        Assert.True(child.GetProperty("canCancel").GetBoolean());
        Assert.True(child.GetProperty("canStop").GetBoolean());
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"native-sibling","state":{"state":"running"},"capabilities":{"cancel":{}}}""", "native-parent");
        await ChildRowAsync(rows, "native-sibling");
        // An omitted capability/state patch retains the provider's resolved advertisement and activity.
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"native-child","description":"retained"}""", "native-parent");
        Assert.True((await ChildRowAsync(rows, "native-child")).GetProperty("canStop").GetBoolean());
        using var socket = await ControlSocketAsync();
        await ControlAsync(socket, 1, parent, "native-child");
        var answer = ControlAnswerAsync(socket, 1);
        Assert.Equal("native-child", (await agent.ReadAsync("session/cancel")).GetProperty("params").GetProperty("sessionId").GetString());
        await agent.TextAsync("Parent is still processing", "parent-alive", "native-parent");
        await ParentTextAsync(rows);
        Assert.False(answer.IsCompleted); // A cancel notification alone is not completion.
        var before = await RowsAsync(remote ? _box : _desktop!, remote ? BoxToken : DesktopToken, parent);
        Assert.Equal(state, before.Single(r => r.GetProperty("itemId").GetString() == "subagent:native-child").GetProperty("payload").GetProperty("state").GetString());
        Assert.Equal("running", before.Single(r => r.GetProperty("itemId").GetString() == "subagent:native-sibling").GetProperty("payload").GetProperty("state").GetString());
        await agent.UpdateAsync("""{"sessionUpdate":"state_update","state":"idle","stopReason":"cancelled"}""", "native-child");
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"native-child","state":{"state":"idle","stopReason":"cancelled"}}""", "native-parent");
        Assert.False((await ChildRowAsync(rows, "native-child")).GetProperty("canStop").GetBoolean());
        Assert.Equal("""{"t":"result","id":1,"result":{"ok":true}}""", (await answer).GetRawText());
        // The association remains reusable; no optimistic removal or parent/sibling cancellation occurred.
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"native-child","state":{"state":"running"}}""", "native-parent");
        Assert.True((await ChildRowAsync(rows, "native-child")).GetProperty("canStop").GetBoolean());
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"native-child","state":{"state":"idle"}}""", "native-parent");
        await ChildRowAsync(rows, "native-child");
        await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"native-sibling","state":{"state":"idle"}}""", "native-parent");
        await ChildRowAsync(rows, "native-sibling");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        Assert.Equal("native-parent", await agent.CloseAsync()); // Next provider write: no extra cancel or child close.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_child_stop_socket_refuses_unadvertised_cleared_unknown_and_generic_work(bool remote)
    {
        var (parent, agent, prompt) = await StartControlParentAsync(remote);
        using var rows = await OpenLiveAsync(_desktop!, DesktopToken, parent, 0);
        await ApiTests.ReadRowsAsync(rows, 2);
        using var socket = await ControlSocketAsync();
        var patches = new[]
        {
            "\"state\":{\"state\":\"running\"}",
            "\"capabilities\":{}",
            "\"capabilities\":{\"cancel\":null}",
            "\"capabilities\":null",
            "\"capabilities\":{\"cancel\":{}},\"state\":null",
            "\"state\":{\"state\":\"future-state\"}",
            "\"state\":{\"state\":\"idle\"}"
        };
        long id = 0;
        foreach (var patch in patches)
        {
            if (id > 0)
            {
                await agent.UpdateAsync("""{"sessionUpdate":"subagent_update","sessionId":"native-child","state":{"state":"running"},"capabilities":{"cancel":{}}}""", "native-parent");
                Assert.True((await ChildRowAsync(rows, "native-child")).GetProperty("canStop").GetBoolean());
            }
            await agent.UpdateAsync("{\"sessionUpdate\":\"subagent_update\",\"sessionId\":\"native-child\"," + patch + "}", "native-parent");
            Assert.False((await ChildRowAsync(rows, "native-child")).GetProperty("canStop").GetBoolean());
            await ControlAsync(socket, ++id, parent, "native-child");
            AssertControlRefusal(await ControlAnswerAsync(socket, id));
        }
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"tool-id","title":"Background work","status":"in_progress"}""", "native-parent");
        while ((await ApiTests.ReadRowsAsync(rows, 1))[0].GetProperty("kind").GetString() != "tool") { }
        foreach (var unsupported in new[] { "unknown", "tool-id" })
        {
            await ControlAsync(socket, ++id, parent, unsupported);
            AssertControlRefusal(await ControlAnswerAsync(socket, id));
        }
        await agent.UpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"tool-id","status":"completed"}""", "native-parent");
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        Assert.Equal("native-parent", await agent.CloseAsync()); // No provider cancellation for any refusal.
    }

    [Fact]
    public async Task Native_child_stop_socket_does_not_treat_an_Assembly_helper_as_a_native_child()
    {
        var (parent, parentAgent, parentPrompt, helperId, helperAgent, helperPrompt) = await StartRemoteHelperOfRemoteParentAsync();
        using var socket = await ControlSocketAsync();
        await ControlAsync(socket, 1, parent, helperId);
        AssertControlRefusal(await ControlAnswerAsync(socket, 1));
        await helperAgent.FinishAsync(helperPrompt, """{"stopReason":"end_turn"}""");
        Assert.Equal("acp-helper", await helperAgent.CloseAsync());
        await parentAgent.FinishAsync(parentPrompt, """{"stopReason":"end_turn"}""");
        await parentAgent.CloseAsync();
    }

    private async Task<(string Parent, FakeLink Agent, JsonElement Prompt)> StartControlParentAsync(bool remote)
    {
        string parent;
        if (remote) parent = await CreateSessionAsync();
        else
        {
            parent = (await FeatureResultAsync(_desktop!, DesktopToken, "sessions", "create",
                new { agent = "claude", clientId = Guid.NewGuid().ToString("N") })).GetProperty("sessionId").GetString()!;
        }
        Assert.NotEqual("native-parent", parent);
        await SendAsync(parent);
        var agent = await (remote ? _boxLauncher : _desktopLauncher).NextAsync();
        await agent.StartAsync("native-parent");
        return (parent, agent, await agent.ReadAsync("session/prompt"));
    }

    private async Task<ClientWebSocket> ControlSocketAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_desktop!.Address.Authority}/socket"), CancellationToken.None);
        await SocketSendAsync(socket, $$"""{"t":"hello","token":"{{DesktopToken}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await SocketReadAsync(socket));
        return socket;
    }

    private static Task ControlAsync(WebSocket socket, long id, string parent, string child) => SocketSendAsync(socket,
        $$$"""{"t":"call","id":{{{id}}},"feature":"send","method":"stopTask","args":{"session":"{{{parent}}}","task":"{{{child}}}"}}""");
    private static async Task<JsonElement> ControlAnswerAsync(WebSocket socket, long id)
    {
        while (true)
        {
            var frame = JsonDocument.Parse(await SocketReadAsync(socket).WaitAsync(TimeSpan.FromSeconds(10))).RootElement.Clone();
            if (frame.TryGetProperty("id", out var found) && found.GetInt64() == id) return frame;
        }
    }
    private static void AssertControlRefusal(JsonElement frame)
    {
        Assert.Equal("failed", frame.GetProperty("code").GetString());
        Assert.Equal("that task isn't running, or can't be stopped", frame.GetProperty("message").GetString());
    }
    private static async Task<JsonElement> ChildRowAsync(ClientWebSocket rows, string child)
    {
        while (true)
        {
            var row = (await ApiTests.ReadRowsAsync(rows, 1))[0];
            if (row.GetProperty("itemId").GetString() == "subagent:" + child) return row.GetProperty("payload");
        }
    }
    private static async Task ParentTextAsync(ClientWebSocket rows)
    {
        while ((await ApiTests.ReadRowsAsync(rows, 1))[0].GetProperty("kind").GetString() != "delta") { }
    }
}
