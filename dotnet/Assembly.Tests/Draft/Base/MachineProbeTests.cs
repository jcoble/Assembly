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
        await using var feature = Feature();

        feature.Tick(true);
        await Until(() => _far.Probes == 1);
        // Its 5 s limit passes, but this machine ignores it, so the probe is still out.
        _time.Advance(TimeSpan.FromSeconds(5));
        for (var tick = 0; tick < 15; tick++)
            feature.Tick(true);
        await Task.Delay(100);
        Assert.Equal(1, _far.Probes);

        answer.SetResult();
        // Every 15th tick tries again, once the first probe has ended.
        for (var round = 0; round < 100 && _far.Probes < 2; round++)
        {
            for (var tick = 0; tick < 15; tick++)
                feature.Tick(true);
            await Task.Delay(10);
        }
        Assert.Equal(2, _far.Probes);
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
        await Until(() => _far.Probes == 1);

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

        Assert.NotSame(probed.Task, await Task.WhenAny(probed.Task, Task.Delay(TimeSpan.FromMilliseconds(300))));
    }

    private static async Task Until(Func<bool> done)
    {
        for (var waited = TimeSpan.Zero; !done(); waited += TimeSpan.FromMilliseconds(10))
        {
            Assert.True(waited < Wait, "it didn't happen within 10 s");
            await Task.Delay(10);
        }
    }
}
