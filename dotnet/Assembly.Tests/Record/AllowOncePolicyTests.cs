using Assembly.Application.Common.Acp.Messages;
using Assembly.Remote.Record;

namespace Assembly.Tests.RemoteRecording;

public sealed class AllowOncePolicyTests
{
    [Fact]
    public async Task Allow_once_takes_priority_over_an_earlier_allow_always_option()
    {
        var policy = new AllowOncePolicy();
        var outcome = await policy.DecideAsync(Request(
            new("always", "Always", "allow_always"),
            new("once", "Once", "allow_once")), CancellationToken.None);

        Assert.Equal(PermissionOutcome.Selected("once"), outcome);
        Assert.Equal(["Read notes"], policy.Asked);
    }

    [Fact]
    public async Task The_first_allow_option_is_used_when_allow_once_is_missing()
    {
        var policy = new AllowOncePolicy();
        var outcome = await policy.DecideAsync(Request(
            new("no", "No", "reject_once"),
            new("always", "Always", "allow_always"),
            new("later", "Later", "allow_future")), CancellationToken.None);

        Assert.Equal(PermissionOutcome.Selected("always"), outcome);
        Assert.Equal(["Read notes"], policy.Asked);
    }

    [Fact]
    public async Task Requests_without_an_allow_option_are_recorded_and_cancelled()
    {
        var policy = new AllowOncePolicy();
        var outcome = await policy.DecideAsync(Request(new PermissionOption("no", "No", "reject_once")), CancellationToken.None);

        Assert.Equal(PermissionOutcome.Cancelled, outcome);
        Assert.Equal(["Read notes"], policy.Asked);
        Assert.Equal(PermissionOutcome.Cancelled, await policy.DecideAsync(Request(), CancellationToken.None));
        Assert.Equal(["Read notes", "Read notes"], policy.Asked);
    }

    private static RequestPermissionRequest Request(params PermissionOption[] options) =>
        new("session", "Read notes", null, null, options);
}
