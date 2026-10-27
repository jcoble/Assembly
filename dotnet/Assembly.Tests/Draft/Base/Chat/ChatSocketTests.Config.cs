using Assembly.Domain.Sessions;

namespace Assembly.Tests.Draft.Base.Chat;

public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task Config_get_and_set_answer_the_sessions_config_from_its_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Config = new SessionConfig("""[{"id":"model"}]""", new Dictionary<string, string> { ["model"] = "big" }, """[{"name":"init"}]""");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "config", "get", $$"""{"session":"{{session.SessionId}}"}""");
        Assert.Equal(
            """{"t":"result","id":1,"result":{"options":[{"id":"model"}],"choices":{"model":"big"},"commands":[{"name":"init"}]}}""",
            await ReadAsync(socket));

        _box.Config = new SessionConfig(null, new Dictionary<string, string> { ["model"] = "small" });
        await CallAsync(socket, 2, "config", "set", $$"""{"session":"{{session.SessionId}}","configId":"model","value":"small"}""");
        Assert.Equal("""{"t":"result","id":2,"result":{"options":null,"choices":{"model":"small"},"commands":null}}""", await ReadAsync(socket));
        Assert.Equal(["config", "set config model small"], _box.Asked);

        await CallAsync(socket, 3, "config", "get", """{"session":"nope"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_cancel_aborts_config_get_on_the_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "config", "get", $$"""{"session":"{{session.SessionId}}"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");

        await _box.Cancelled.Task.WaitAsync(Wait);
        Assert.Contains("\"code\":\"cancelled\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task Closing_the_socket_aborts_config_get_on_the_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Hold();
        var socket = await OpenAsync();

        await CallAsync(socket, 1, "config", "get", $$"""{"session":"{{session.SessionId}}"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        socket.Abort();
        socket.Dispose();

        await _box.Cancelled.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task Machines_list_is_gone_and_agents_answers_a_machines_agents()
    {
        using var socket = await OpenAsync();

        // The machines topic carries the list now (MachinesSocketTests).
        await CallAsync(socket, 1, "machines", "list", "{}");
        var gone = await ReadAsync(socket);
        Assert.Contains("\"code\":\"not_found\"", gone);
        Assert.Contains("there's no call machines/list", gone);
        await CallAsync(socket, 2, "machines", "agents", """{"machine":"box"}""");
        Assert.Equal("""{"t":"result","id":2,"result":{"agents":[{"id":"claude","name":"Claude"}]}}""", await ReadAsync(socket));
        Assert.Equal(["agents"], _box.Asked);
        await CallAsync(socket, 3, "machines", "agents", "{}");
        Assert.Contains("\"agents\":[", await ReadAsync(socket));
        Assert.Equal(["agents"], _here.Asked);

        await CallAsync(socket, 4, "machines", "agents", """{"machine":"nope"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task A_cancel_aborts_machines_agents_on_the_machine()
    {
        _box.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "machines", "agents", """{"machine":"box"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");

        await _box.Cancelled.Task.WaitAsync(Wait);
        Assert.Contains("\"code\":\"cancelled\"", await ReadAsync(socket));
    }

    [Fact]
    public async Task Closing_the_socket_aborts_machines_agents_on_the_machine()
    {
        _box.Hold();
        var socket = await OpenAsync();

        await CallAsync(socket, 1, "machines", "agents", """{"machine":"box"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        socket.Abort();
        socket.Dispose();

        await _box.Cancelled.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task Config_get_set_and_machines_agents_on_a_machine_thats_down_are_unreachable()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Down = true;
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "config", "get", $$"""{"session":"{{session.SessionId}}"}""");
        Assert.Equal("""{"t":"error","id":1,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
        await CallAsync(socket, 2, "config", "set", $$"""{"session":"{{session.SessionId}}","configId":"model","value":"small"}""");
        Assert.Equal("""{"t":"error","id":2,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
        await CallAsync(socket, 3, "machines", "agents", """{"machine":"box"}""");
        Assert.Equal("""{"t":"error","id":3,"code":"unreachable","message":"box is down","machine":"box"}""", await ReadAsync(socket));
    }

    [Fact]
    public async Task Config_set_fails_for_an_unknown_session()
    {
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "config", "set", """{"session":"nope","configId":"model","value":"small"}""");

        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"unknown session"}""", await ReadAsync(socket));
        Assert.Empty(_here.Asked);
        Assert.Empty(_box.Asked);
    }

    [Fact]
    public async Task A_cancel_aborts_config_set_on_the_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        _box.Hold();
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "config", "set", $$"""{"session":"{{session.SessionId}}","configId":"model","value":"small"}""");
        await _box.Reached.Task.WaitAsync(Wait);
        await SendAsync(socket, """{"t":"cancel","id":1}""");

        await _box.Cancelled.Task.WaitAsync(Wait);
        Assert.Equal("""{"t":"error","id":1,"code":"cancelled","message":"config/set was cancelled"}""", await ReadAsync(socket));
    }
}
