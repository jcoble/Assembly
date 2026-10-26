using Assembly.Infrastructure.Diagnostics;
using Assembly.Infrastructure.Http;

using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;
namespace Assembly.Tests.Features.Diagnostics;

public sealed class TurnActivityTests
{
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
