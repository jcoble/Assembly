namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task SendAfterTurnWaitsForTheRunningTurn()
    {
        var first = _host.Send("held");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        var sending = _host.SendAfterTurnAsync("answer", "answer-id", CancellationToken.None);
        Assert.False(sending.IsCompleted);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        // Close must be the next request: a steering request fails this assertion.
        await agent.CloseAsync();
        var run = await sending.WaitAsync(Wait);
        Assert.NotNull(run);
        Assert.NotEqual(first, run);
        await agent.OpenAsync();
        var next = await agent.ReadAsync("session/prompt");
        Assert.Equal("answer", next.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(run, await _host.SendAfterTurnAsync("answer", "answer-id", CancellationToken.None));
        await agent.ReplyAsync(next, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task SendAfterTurnStartsAtOnceWhenIdle()
    {
        var sending = _host.SendAfterTurnAsync("answer", null, CancellationToken.None);
        Assert.True(sending.IsCompletedSuccessfully);
        Assert.Equal(_host.ActiveRunId, await sending);
        Assert.NotNull(await sending);
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal("answer", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
    }

    [Fact]
    public async Task SendAfterTurnHonoursCancellation()
    {
        _host.Send("held");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        using var cancel = new CancellationTokenSource();
        var sending = _host.SendAfterTurnAsync("answer", null, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        Assert.Single(_store.RowsAfter(_session.SessionId, 0), row => row.Kind == "user");
    }
}
