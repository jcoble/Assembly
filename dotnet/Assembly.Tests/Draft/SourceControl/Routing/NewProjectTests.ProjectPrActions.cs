using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.GitHub;
using Assembly.Infrastructure.Git;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class NewProjectTests
{
    [Fact]
    public async Task A_sessionless_project_comment_runs_once_on_the_captured_remote_owner()
    {
        var desktopGh = new ProjectGh(); var ownerGh = new ProjectGh();
        await using var h = await Harness.Start(localGhClient: desktopGh, remoteGhClient: ownerGh);
        var git = new GitCli(); await new GitWriter(git).InitAsync(h.RemoteParent, default);
        Assert.Equal(0, (await git.RunAsync(h.RemoteParent, ["remote", "add", "origin", "https://github.com/owner/repo"], null, null, default)).ExitCode);
        var project = h.Hosts.Mac.Fixture.Db.Writer.Write("fixture.project", scope => h.Store.AddOrRestore(scope,
            "workbox-test", h.RemoteParent, "Remote project", "github.com/owner/repo").Project);
        var source = h.Hosts.Mac.Fixture.Db.QueryFirst("SELECT machine,generation,name FROM engine_mirror WHERE state='live' AND name='workbox-test'",
            row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default)!;
        using var client = await h.Open();
        var host = (await h.Call(client, 1, "operation_host", new GitOperationHostArgs(Action: "comment", ExpectedSource: source, ProjectId: project.Id))).GetProperty("result");
        Assert.Equal(h.Hosts.Remote.Fixture.Machine, host.GetProperty("machine").GetString());
        var id = Guid.NewGuid();
        var request = new GitOperationStart(host.GetProperty("instanceId").GetString()!, id, null, "comment",
            JsonSerializer.SerializeToElement(new { number = 7, expectedHead = new string('a', 40), body = "Project comment" }),
            ExpectedSource: source, ExpectedCheckoutRoot: h.RemoteParent, ProjectId: project.Id, ExpectedRepository: "owner/repo");
        Assert.Equal("error", (await h.Call(client, 6, "start", request)).GetProperty("t").GetString());
        Assert.Equal(0, ownerGh.Reads); Assert.Equal(0, ownerGh.Writes);
        await h.Subscribe(client, id, source.Machine);
        Assert.Equal("result", (await h.Call(client, 2, "start", request)).GetProperty("t").GetString());
        var terminal = await h.Terminal(client);
        Assert.Equal("succeeded", terminal.GetProperty("state").GetString());
        Assert.Equal(source.Machine, terminal.GetProperty("machine").GetString());
        Assert.Equal("https://github.com/owner/repo/pull/7#issuecomment-1", terminal.GetProperty("result").GetString());
        Assert.Equal(1, ownerGh.Writes); Assert.Equal(0, desktopGh.Reads); Assert.Equal(0, desktopGh.Writes);
        Assert.All(ownerGh.Roots, root => Assert.Equal(h.RemoteParent, root));
        Assert.Equal("operation_missing", h.LocalOperations.Status(id).Error);
        h.Hosts.Mac.Fixture.Db.Writer.Write("fixture.remove", scope => h.Store.Remove(scope, project.Id, false));
        var retained = new GitOperationArgs(id, Action: "comment", InstanceId: request.InstanceId, ExpectedSource: source, ProjectId: project.Id);
        var status = (await h.Call(client, 3, "operation", retained)).GetProperty("result");
        Assert.Equal("succeeded", status.GetProperty("state").GetString());
        Assert.Equal("result", (await h.Call(client, 4, "release", retained)).GetProperty("t").GetString());
        var refused = await h.Call(client, 5, "start", request with { OperationId = Guid.NewGuid() });
        Assert.Equal("source_changed", refused.GetProperty("code").GetString());
        Assert.Equal(1, ownerGh.Writes);
        Assert.Equal(0L, h.Hosts.Mac.Fixture.Db.QueryFirst("SELECT COUNT(*) FROM session_info", row => row.Long(0), default));
        Assert.Equal(0L, h.Hosts.Remote.Fixture.Db.QueryFirst("SELECT COUNT(*) FROM session_info", row => row.Long(0), default));
    }

    private sealed class ProjectGh : IGh
    {
        internal int Reads, Writes;
        internal readonly List<string> Roots = [];
        public Task<GhRun> RunAsync(string root, IReadOnlyList<string> args, string? input, TimeSpan limit, CancellationToken ct)
        {
            Roots.Add(root);
            if (args.Contains("POST"))
            {
                Writes++;
                Assert.Contains("repos/owner/repo/issues/7/comments", args);
                using var payload = JsonDocument.Parse(input!);
                Assert.Equal("Project comment", payload.RootElement.GetProperty("body").GetString());
                return Task.FromResult(new GhRun(0, """{"html_url":"https://github.com/owner/repo/pull/7#issuecomment-1"}""", ""));
            }
            Reads++;
            return Task.FromResult(new GhRun(0, args[0] == "pr" ? """{"number":7,"headRefOid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseRefOid":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN"}""" : "[]", ""));
        }
    }
}
