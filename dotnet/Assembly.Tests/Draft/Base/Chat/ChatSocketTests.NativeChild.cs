using System.Text.Json;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    [Theory]
    [InlineData("{\"session\":\"parent\"}")]
    [InlineData("{\"task\":\"child\"}")]
    public async Task Native_child_stop_requires_both_generated_arguments(string args)
    {
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "send", "stopTask", args);
        Assert.Contains("\"code\":\"bad_args\"", await ReadAsync(socket));
        Assert.Empty(_here.Asked);
        Assert.Empty(_box.Asked);
    }

    [Fact]
    public void Native_child_stop_arguments_use_the_existing_generated_CamelCase_contract()
    {
        var args = new SendFeature.StopTaskArgs("assembly-parent", "native-child");
        var json = JsonSerializer.Serialize(args, SessionsJson.Default.StopTaskArgs);
        Assert.Equal("""{"session":"assembly-parent","task":"native-child"}""", json);
        Assert.Equal(args, JsonSerializer.Deserialize(json, SessionsJson.Default.StopTaskArgs));
    }

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
        Assert.Equal(["stop task native-child", $"send {parent.SessionId} after cancellation"], _box.Asked);
    }
}
