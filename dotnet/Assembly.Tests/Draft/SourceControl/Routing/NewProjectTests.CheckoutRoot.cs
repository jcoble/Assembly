using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.SourceControl;
using Assembly.Infrastructure.Chat;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class NewProjectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_changed_checkout_refuses_the_observed_root_even_when_head_and_status_match(bool operation)
    {
        var git = new BlockedFetch();
        await using var h = await Harness.Start(localGit: git);
        var realGit = new GitCli();
        var seed = await h.CloneSource();
        foreach (var destination in new[] { h.LocalParent, h.RemoteParent })
            Assert.Equal(0, (await realGit.RunAsync(Path.GetDirectoryName(destination)!, ["clone", "--", seed, destination], null, null, default)).ExitCode);
        await File.WriteAllTextAsync(Path.Combine(h.LocalParent, "same.txt"), "same content");
        await File.WriteAllTextAsync(Path.Combine(h.RemoteParent, "same.txt"), "same content");
        var owned = h.Hosts.Mac.Sessions.Create("fixture", "", workingFolder: h.LocalParent);
        using var client = await h.Open();
        var origin = new { machine = h.Hosts.Mac.Fixture.Machine, generation = 0, routeMachine = (string?)null };
        var observed = (await h.Call(client, 1, "status", new { session = owned.SessionId, expectedSource = origin })).GetProperty("result");
        var observedRoot = observed.GetProperty("checkoutRoot").GetString()!;
        var replacement = await new GitStatusReader(realGit).ReadAsync(h.RemoteParent, default);
        Assert.NotNull(replacement.Head);
        Assert.Equal(replacement.Head, observed.GetProperty("head").GetString());
        Assert.Equal(replacement.Fingerprint, observed.GetProperty("fingerprint").GetString());
        h.Hosts.Mac.Fixture.Db.Writer.Write("move only the fixture session checkout", scope => scope.Execute(
            "UPDATE session_info SET working_folder=$root WHERE session_id=$id",
            new SqlArg("$root", h.RemoteParent), new SqlArg("$id", owned.SessionId)));
        Assert.Equal(h.RemoteParent, SessionPaths.RootOf(h.Hosts.Mac.Sessions, owned.SessionId));
        var id = Guid.NewGuid();
        var refused = operation
            ? await h.Call(client, 2, "start", new
            {
                instanceId = h.LocalOperations.InstanceId,
                operationId = id,
                session = owned.SessionId,
                action = "fetch",
                args = new { },
                expectedSource = origin,
                expectedCheckoutRoot = observedRoot
            })
            : await h.Call(client, 2, "stage", new
            {
                session = owned.SessionId,
                paths = new[] { "same.txt" },
                expectedSource = origin,
                expectedCheckoutRoot = observedRoot
            });
        Assert.Equal("source_changed", refused.GetProperty("code").GetString());
        Assert.Equal("", (await realGit.RunAsync(h.LocalParent, ["diff", "--cached", "--name-only"], null, null, default)).Output);
        Assert.Equal("", (await realGit.RunAsync(h.RemoteParent, ["diff", "--cached", "--name-only"], null, null, default)).Output);
        Assert.Equal(0, git.Fetches);
        Assert.Equal("operation_missing", h.LocalOperations.Status(id).Error);
    }
}
