using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class NewProjectTests
{
    [Fact]
    public async Task A_remote_fetch_pushes_its_actual_operation_without_a_desktop_execution_copy()
    {
        await using var h = await Harness.Start();
        var source = await h.CloneSource();
        var git = new GitCli();
        await new GitWriter(git).InitAsync(h.RemoteParent, default);
        Assert.Equal(0, (await git.RunAsync(h.RemoteParent, ["remote", "add", "origin", source], null, null, default)).ExitCode);
        var owned = h.Hosts.Remote.Sessions.Create("fixture", "", workingFolder: h.RemoteParent);
        h.Hosts.Mac.Sessions.Add(owned.SessionId, "fixture", "", "workbox-test", workingFolder: h.LocalParent);
        using var client = await h.Open();
        var host = (await h.Call(client, 1, "operation_host", new { session = owned.SessionId, action = "fetch" })).GetProperty("result");
        Assert.Equal(h.Hosts.Remote.Fixture.Machine, host.GetProperty("machine").GetString());
        var id = Guid.NewGuid();
        await h.Subscribe(client, id, h.Hosts.Remote.Fixture.Machine);
        var request = new GitOperationStart(host.GetProperty("instanceId").GetString()!, id, owned.SessionId, "fetch",
            System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone());
        Assert.Equal("result", (await h.Call(client, 2, "start", request)).GetProperty("t").GetString());
        var terminal = await h.Terminal(client);
        Assert.Equal("succeeded", terminal.GetProperty("state").GetString());
        Assert.Equal(id, terminal.GetProperty("operationId").GetGuid());
        Assert.Equal(h.Hosts.Remote.Fixture.Machine, terminal.GetProperty("machine").GetString());
        Assert.Equal("operation_missing", h.LocalOperations.Status(id).Error);
        Assert.NotEqual("", (await git.RunAsync(h.RemoteParent, ["for-each-ref", "refs/remotes/origin"], null, null, default)).Output);
    }

    [Fact]
    public async Task A_remote_session_status_and_stage_use_only_its_actual_owner_checkout()
    {
        await using var h = await Harness.Start();
        var git = new GitCli();
        await new GitWriter(git).InitAsync(h.LocalParent, default);
        await new GitWriter(git).InitAsync(h.RemoteParent, default);
        await File.WriteAllTextAsync(Path.Combine(h.LocalParent, "local.txt"), "desktop");
        await File.WriteAllTextAsync(Path.Combine(h.RemoteParent, "remote ü.txt"), "owner");
        var owned = h.Hosts.Remote.Sessions.Create("fixture", "", workingFolder: h.RemoteParent);
        h.Hosts.Mac.Sessions.Add(owned.SessionId, "fixture", "", "workbox-test", workingFolder: h.LocalParent);
        using var client = await h.Open();
        var before = await h.Call(client, 1, "status", new { session = owned.SessionId });
        Assert.Equal("result", before.GetProperty("t").GetString());
        Assert.Contains(before.GetProperty("result").GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == "remote ü.txt");
        Assert.DoesNotContain(before.GetProperty("result").GetProperty("files").EnumerateArray(), file => file.GetProperty("path").GetString() == "local.txt");
        var staged = await h.Call(client, 2, "stage", new { session = owned.SessionId, paths = new[] { "remote ü.txt" } });
        Assert.Equal("result", staged.GetProperty("t").GetString());
        Assert.Equal("remote ü.txt", (await git.RunAsync(h.RemoteParent, ["diff", "--cached", "--name-only"], null, null, default)).Output.Trim());
        Assert.Equal("", (await git.RunAsync(h.LocalParent, ["diff", "--cached", "--name-only"], null, null, default)).Output);
        var overrideOwner = await h.Call(client, 3, "operation_host", new { session = owned.SessionId, action = "push", machine = h.Hosts.Remote.Fixture.Machine });
        Assert.Equal("failed", overrideOwner.GetProperty("code").GetString());
    }
}
