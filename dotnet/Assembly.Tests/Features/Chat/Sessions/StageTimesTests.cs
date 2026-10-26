using Microsoft.Extensions.Time.Testing;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests.Features.Chat.Sessions;

public sealed class StageTimesTests
{
    [Fact]
    public void Stages_are_ms_since_the_send_in_the_order_reached_and_each_counts_once()
    {
        var time = new FakeTimeProvider();
        var stages = new StageTimes(time);
        Assert.Equal("", stages.ToString());

        time.Advance(TimeSpan.FromMilliseconds(812));
        stages.Mark("clone");
        time.Advance(TimeSpan.FromMilliseconds(3));
        stages.Mark("adapter");
        time.Advance(TimeSpan.FromMilliseconds(5));
        stages.Mark("adapter");

        Assert.Equal("clone 812 ms, adapter 815 ms", stages.ToString());
    }
}
