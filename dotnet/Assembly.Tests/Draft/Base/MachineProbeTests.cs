using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Draft.Base.Chat;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>The machines probe, driven tick by tick, with a machine that can ignore the probe's token.</summary>
public sealed class MachineProbeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeMachine _far = new("far", null!);

    private MachinesFeature Feature() =>
        new(new MachineSet(new FakeMachine(null, null!), _far), new MachineStatus(new TopicSet(), ["far"]), () => { }, _time);

    [Fact]
    public async Task A_probe_still_out_on_the_next_15th_tick_is_not_doubled()
    {
        var answer = new TaskCompletionSource();
        _far.Probe = _ => answer.Task;
        var feature = Feature();

        feature.Tick(true);
        await _far.Probes.ReachedAsync(1);
        // Its 5 s limit passes, but this machine ignores it, so the probe is still out.
        _time.Advance(TimeSpan.FromSeconds(5));
        for (var tick = 0; tick < 15; tick++)
            feature.Tick(true);

        // Set off the test's thread, whose context would queue the probe's continuation: here it runs inside SetResult,
        // so the first probe has ended once this returns.
        await Task.Run(answer.SetResult);
        // Every 15th tick tries again, once the first probe has ended.
        for (var tick = 0; tick < 15; tick++)
            feature.Tick(true);
        await _far.Probes.ReachedAsync(2);

        // The dispose waits for the last probe started; a doubled one would have made it the third.
        await feature.DisposeAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(2, _far.Probes.Value);
    }

    [Fact]
    public async Task Disposing_with_a_probe_out_cancels_it_and_waits_for_it()
    {
        var ended = false;
        _far.Probe = async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                await Task.Yield();
                ended = true;
            }
        };
        var feature = Feature();
        feature.Tick(true);
        await _far.Probes.ReachedAsync(1);

        await feature.DisposeAsync().AsTask().WaitAsync(Wait);

        Assert.True(ended);
    }

    [Fact]
    public async Task A_tick_after_dispose_starts_no_probe()
    {
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _far.Probe = _ =>
        {
            probed.TrySetResult();
            return Task.CompletedTask;
        };
        var feature = Feature();
        await feature.DisposeAsync();

        // A tick that was already on its way when the backend stopped.
        feature.Tick(true);

        // A second dispose waits for any probe the tick started.
        await feature.DisposeAsync().AsTask().WaitAsync(Wait);
        Assert.False(probed.Task.IsCompleted);
    }
}
