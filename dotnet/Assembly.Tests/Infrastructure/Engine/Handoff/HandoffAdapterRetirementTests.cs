using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Features.Chat.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Assembly.Tests.Infrastructure.Engine.Handoff;

public sealed class HandoffAdapterRetirementTests
{
    [Fact]
    public async Task Other_execution_owners_prevent_positive_disposal_without_killing_their_adapter()
    {
        var time = new FakeTimeProvider(); var launcher = new FakeLauncher();
        await using var pool = new SharedAdapter(launcher, "claude", time, TextWriter.Null);
        var opening = pool.GetAsync(new StageTimes(time), default);
        var agent = await launcher.NextAsync(); await SessionWarmConfigTests.Initialize(agent);
        var first = await opening;
        var second = await pool.GetAsync(new StageTimes(time), default);
        Assert.Same(first, second);

        Assert.False(await pool.RetireAsync(first, "public-acp"));
        Assert.False(agent.Disposed);
        await pool.ReleaseAsync(first);
        Assert.False(agent.Disposed);
        await pool.ReleaseAsync(second);
        Assert.True(agent.Disposed);
        Assert.False(await pool.RetireAsync(first, "public-acp"));
    }

    [Fact]
    public async Task Only_the_call_that_joins_an_actual_owned_stop_reports_disposal()
    {
        var time = new FakeTimeProvider(); var launcher = new FakeLauncher();
        await using var pool = new SharedAdapter(launcher, "claude", time, TextWriter.Null);
        var opening = pool.GetAsync(new StageTimes(time), default);
        var agent = await launcher.NextAsync(); await SessionWarmConfigTests.Initialize(agent);
        var running = await opening;
        await pool.ReleaseAsync(running);
        Assert.False(agent.Disposed);

        Assert.True(await pool.RetireAsync(running, "public-acp"));

        Assert.True(agent.Disposed);
        Assert.True(agent.Exited.IsCompletedSuccessfully);
        Assert.False(await pool.RetireAsync(running, "public-acp"));
    }
}
