using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Tests.Draft.Base.Chat;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.Base;

/// <summary>The machines probe, driven tick by tick, with a machine that can ignore the probe's token.</summary>
public sealed class MachineProbeTests
{
    [Fact]
    public void A_probe_still_out_on_the_next_15th_tick_is_not_doubled()
    {
        var time = new FakeTimeProvider();
        var far = new FakeMachine("far", null!);
        var answer = new TaskCompletionSource();
        far.ProbeAnswer = answer.Task;
        var feature = new MachinesFeature(new MachineSet(new FakeMachine(null, null!), far), new MachineStatus(new TopicSet(), ["far"]), () => { }, time);

        feature.Tick(true);
        Assert.Equal(1, far.Probes);
        // Its 5 s limit passes, but this machine ignores it, so the probe is still out.
        time.Advance(TimeSpan.FromSeconds(5));
        for (var tick = 0; tick < 15; tick++)
            feature.Tick(true);
        Assert.Equal(1, far.Probes);

        answer.SetResult();
        for (var tick = 0; tick < 15; tick++)
            feature.Tick(true);
        Assert.Equal(2, far.Probes);
    }
}
