using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task Native_child_stop_and_send_share_the_parent_session_order_key()
    {
        var parent = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Hold();
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "send", "stopTask", $$"""{"session":"{{parent.SessionId}}","task":"native-child"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await CallAsync(socket, 2, "send", "send", $$"""{"session":"{{parent.SessionId}}","text":"after cancellation"}""");
        await CallAsync(socket, 3, "preferences", "set", Save);
        Assert.Equal([3], (await ReadAnswersAsync(socket, 1)).Keys);
        Assert.Equal(["stop task native-child"], _box.Asked);
        _box.Release();
        Assert.Equal([1, 2], (await ReadAnswersAsync(socket, 2)).Keys.Order());
        Assert.Equal(["stop task native-child", $"owner prompt {parent.SessionId} after cancellation"], _box.Asked);
    }
}
