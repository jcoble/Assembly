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
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        // Close must be the next request: a steering request fails this assertion.
        await agent.CloseAsync();
        await agent.OpenAsync();
        var next = await agent.ReadAsync("session/prompt");
        Assert.Equal("answer", next.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.False(sending.IsCompleted);
        await agent.FinishAsync(next, """{"stopReason":"end_turn"}""");
        var run = await sending.WaitAsync(Wait);
        Assert.NotNull(run); Assert.NotEqual(first, run);
        Assert.Equal(run, await _host.SendAfterTurnAsync("answer", "answer-id", CancellationToken.None));
        await agent.CloseAsync();
    }

    [Fact]
    public async Task SendAfterTurnStartsAtOnceWhenIdle()
    {
        var sending = _host.SendAfterTurnAsync("answer", null, CancellationToken.None);
        Assert.False(sending.IsCompleted);
        var pendingRun = Assert.Single(_store.RowsAfter(_session.SessionId, 0), row => row.Kind == "user").RunId;
        var agent = await _launcher.NextAsync();
        await agent.StartAsync();
        var prompt = await agent.ReadAsync("session/prompt");
        Assert.Equal("answer", prompt.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        Assert.Equal(pendingRun, await sending.WaitAsync(Wait));
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
        await agent.FinishAsync(prompt, """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        Assert.Single(_store.RowsAfter(_session.SessionId, 0), row => row.Kind == "user");
    }
}
