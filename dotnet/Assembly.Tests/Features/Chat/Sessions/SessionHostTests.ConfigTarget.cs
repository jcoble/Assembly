using Assembly.Application.Common.Calls;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed partial class SessionHostTests
{
    [Fact]
    public async Task A_targeted_live_pick_refuses_the_old_running_target_before_sending_a_config_request()
    {
        _host.Send("hi");
        var agent = await _launcher.NextAsync();
        await agent.StartAsync(configOptions: Options("opus", "low"));
        var prompt = await agent.ReadAsync("session/prompt");
        _launcher.Agents = [_launcher.Agents[0] with { Args = ["replacement"] }];
        var current = _launcher.ConfigTargets[0].TargetId!;
        await Assert.ThrowsAsync<CallFailed>(() => _host.SetConfigAsync("model", "haiku", CancellationToken.None, current));
        Assert.Empty(_sessionStore.Config(_session.SessionId).Choices);
        await agent.ReplyAsync(prompt, """{"stopReason":"end_turn"}""");
        // The very next request must be normal close, proving no old-instance config request was emitted.
        await agent.CloseAsync();
    }
}
