using System.Net;
using System.Net.WebSockets;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteMachineTests
{
    [Fact]
    public async Task Stop_on_a_remote_parent_s_own_remote_helper_stops_it_on_the_box()
    {
        var (parent, _, _, helperId, helperAgent, _) = await StartRemoteHelperOfRemoteParentAsync();

        using var http = Client(_desktop!, DesktopToken);
        var stopped = await http.PostAsync($"/sessions/{parent}/tasks/{helperId}/stop", null);

        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.Equal("acp-helper", (await helperAgent.ReadAsync("session/cancel")).GetProperty("params").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task StopTask_on_a_remote_parent_s_own_remote_helper_stops_it_on_the_box()
    {
        var (parent, _, _, helperId, helperAgent, _) = await StartRemoteHelperOfRemoteParentAsync();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{_desktop!.Address.Authority}/socket"), CancellationToken.None);
        await SocketSendAsync(socket, $$"""{"t":"hello","token":"{{DesktopToken}}","protocol":1}""");
        Assert.StartsWith("""{"t":"welcome""", await SocketReadAsync(socket));
        await SocketSendAsync(socket, $$$"""{"t":"call","id":1,"feature":"send","method":"stopTask","args":{"session":"{{{parent}}}","task":"{{{helperId}}}"}}""");
        string result;
        while (!(result = await SocketReadAsync(socket)).Contains("\"id\":1"))
        {
        }

        Assert.Equal("""{"t":"result","id":1,"result":{"ok":true}}""", result);
        Assert.Equal("acp-helper", (await helperAgent.ReadAsync("session/cancel")).GetProperty("params").GetProperty("sessionId").GetString());
    }
}
