using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Http;

using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;
namespace Assembly.Tests.Features.Diagnostics;

public sealed class TurnActivityTests
{
    // On every machine: the backend holds it from the turn rows, so it runs inside the hub's save on Linux too, where
    // only the flag changes.
    [Fact]
    public void Hold_holds_and_releases_on_every_machine()
    {
        var activity = new TurnActivity();
        activity.Hold(true);
        activity.Hold(true);
        Assert.True(activity.Held);
        activity.Hold(false);
        Assert.False(activity.Held);
    }

    // The real NSProcessInfo activity, begun and ended.
    [MacFact]
    public void Hold_begins_and_ends_the_macs_activity()
    {
        var activity = new TurnActivity();
        activity.Hold(false);
        activity.Hold(true);
        activity.Hold(true);
        Assert.True(activity.Held);
        activity.Hold(false);
        activity.Hold(false);
        Assert.False(activity.Held);
    }
}
