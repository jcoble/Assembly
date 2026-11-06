using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class NewProjectTests
{
    [Fact]
    public async Task A_qualified_short_write_requires_the_original_observed_checkout_before_effects()
    {
        await using var h = await Harness.Start();
        var git = new GitCli();
        await new GitWriter(git).InitAsync(h.LocalParent, default);
        await File.WriteAllTextAsync(Path.Combine(h.LocalParent, "kept.txt"), "kept");
        var session = h.Hosts.Mac.Sessions.Create("fixture", "", workingFolder: h.LocalParent).SessionId;
        using var client = await h.Open();
        var refused = await h.Call(client, 1, "stage", new
        {
            session,
            paths = new[] { "kept.txt" },
            expectedSource = new ExpectedSource(h.Hosts.Mac.Fixture.Machine, 0, null)
        });
        Assert.Equal("source_changed", refused.GetProperty("code").GetString());
        Assert.Equal("", (await git.RunAsync(h.LocalParent, ["diff", "--cached", "--name-only"], null, null, default)).Output);
    }

    [Theory]
    [InlineData("branches")]
    [InlineData("stage")]
    [InlineData("operation_host")]
    [InlineData("start")]
    public async Task A_remote_git_call_cannot_upgrade_an_absent_original_source(string method)
    {
        var git = new BlockedFetch();
        await using var h = await Harness.Start(remoteGit: git);
        await new GitWriter(new GitCli()).InitAsync(h.RemoteParent, default);
        await File.WriteAllTextAsync(Path.Combine(h.RemoteParent, "kept.txt"), "kept");
        var owned = h.Hosts.Remote.Sessions.Create("fixture", "", workingFolder: h.RemoteParent);
        h.Hosts.Mac.Sessions.Add(owned.SessionId, "fixture", "", "workbox-test", workingFolder: h.LocalParent);
        using var client = await h.Open();
        object args = method switch
        {
            "stage" => new { session = owned.SessionId, paths = new[] { "kept.txt" }, expectedCheckoutRoot = h.RemoteParent },
            "start" => new { session = owned.SessionId, instanceId = h.RemoteOperations.InstanceId, operationId = Guid.NewGuid(), action = "fetch", args = new { }, expectedCheckoutRoot = h.RemoteParent },
            _ => new { session = owned.SessionId, action = "fetch" }
        };
        var refused = await h.Call(client, 1, method, args);
        Assert.Equal("source_changed", refused.GetProperty("code").GetString());
        Assert.Equal(0, git.Fetches);
        Assert.Equal("", (await new GitCli().RunAsync(h.RemoteParent, ["diff", "--cached", "--name-only"], null, null, default)).Output);
    }
}
