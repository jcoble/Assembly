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

    [MacFact]
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
