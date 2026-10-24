using Assembly.Mac.Page;

namespace Assembly.Tests.Mac;

public sealed class TurnActivityTests
{
    [Fact]
    public void Begin_is_idempotent_and_end_releases_it()
    {
        var activity = new TurnActivity();
        activity.End();
        Assert.False(activity.Held);
        activity.Begin();
        activity.Begin();
        Assert.True(activity.Held);
        activity.End();
        Assert.False(activity.Held);
        activity.End();
        Assert.False(activity.Held);
    }
}
