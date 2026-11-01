namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_commands_are_saved_then_updates_and_resume_replace_them(bool clearOnResume)
    {
        _host.Send("one");
        var agent = await _launcher.NextAsync();
        await agent.ReplyAsync(await agent.ReadAsync("initialize"), """{"protocolVersion":2,"capabilities":{},"authMethods":[]}""");
        await agent.ReplyAsync(await agent.ReadAsync("session/new"), """{"sessionId":"acp-1","configOptions":[],"availableCommands":[{"name":"initial","type":"command","description":"Initial"}]}""");
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Contains("initial", _sessionStore.Config(_session.SessionId).Commands ?? "");
        await agent.UpdateAsync("""{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"later","type":"command","description":"Later"}]}""", "acp-1");
        await Until(() => _sessionStore.Config(_session.SessionId).Commands?.Contains("later") == true);
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        await Until(() => _host.ActiveRunId is null);
        _host.Send("two");
        await agent.ReplyAsync(await agent.ReadAsync("session/resume"), clearOnResume
            ? """{"configOptions":[],"availableCommands":[]}""" : """{"configOptions":[]}""");
        var second = await agent.ReadAsync("session/prompt");
        if (clearOnResume) Assert.Equal("[]", _sessionStore.Config(_session.SessionId).Commands);
        else Assert.Contains("later", _sessionStore.Config(_session.SessionId).Commands ?? "");
        await agent.FinishAsync(second, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }
}
