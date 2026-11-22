using System.Text.Json;
using Assembly.Domain.Sessions;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task Composer_port_cancel_reaches_the_owned_discovery()
    {
        _box.Hold();
        using var socket = await OpenAsync();
        await CallAsync(socket, 1, "config", "discover", """{"agent":"claude","targetId":"selected-target","cwd":"/fixture/folder","machine":"box"}""");
        var read = ReadAsync(socket);
        var first = await Task.WhenAny(_box.Reached.Task, read).WaitAsync(Wait);
        if (ReferenceEquals(first, read))
        {
            using var early = JsonDocument.Parse(await read);
            Assert.Equal("cancelled", early.RootElement.GetProperty("code").GetString());
        }
        await _box.Reached.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");
        await _box.Cancelled.Task.WaitAsync(Wait);
        Assert.Contains("\"code\":\"cancelled\"", await read);
        Assert.Empty(_sessions.List(null, 50, default).Sessions);
    }
}
