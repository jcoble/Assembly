namespace Assembly.Tests.Draft.Base.Chat;

// The args each HTTP route turns down are failed here too, before any machine is asked.
public sealed partial class ChatSocketTests
{
    [Fact]
    public async Task An_answer_that_isnt_an_object_fails_without_asking_the_machine()
    {
        var session = _sessions.Add(Guid.NewGuid().ToString(), "claude", "repo", "box");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "send", "answer", $$"""{"session":"{{session.SessionId}}","ask":"ask-1","answer":"yes"}""");

        Assert.Equal("""{"t":"error","id":1,"code":"failed","message":"expected { optionId } or { action, content }"}""", await ReadAsync(socket));
        Assert.Empty(_box.Asked);
    }

    [Fact]
    public async Task Empty_names_fail_without_asking_the_machine()
    {
        var session = _sessions.Create("claude", "repo");
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "config", "set", $$"""{"session":"{{session.SessionId}}","configId":"","value":"x"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
        await CallAsync(socket, 2, "sessions", "create", """{"agent":"claude","repoUrl":""}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));
        await CallAsync(socket, 3, "sessions", "create", """{"agent":"","repoUrl":"repo"}""");
        Assert.Contains("\"code\":\"failed\"", await ReadAsync(socket));

        Assert.Empty(_here.Asked);
    }

    [Fact]
    public async Task An_empty_machine_name_is_this_machine()
    {
        using var socket = await OpenAsync();

        await CallAsync(socket, 1, "machines", "agents", """{"machine":""}""");

        Assert.Contains("\"agents\":[", await ReadAsync(socket));
        Assert.Equal(["agents"], _here.Asked);
    }
}
