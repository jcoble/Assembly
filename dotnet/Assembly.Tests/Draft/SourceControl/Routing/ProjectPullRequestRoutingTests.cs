using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.GitHub;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
using Assembly.Application.Features.Projects;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Infrastructure.Engine.Sessions;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Projects;
using Assembly.Tests.Draft.Base.Chat;
using Assembly.Tests.Infrastructure.Http;
using Assembly.Tests.Infrastructure.Remote;

namespace Assembly.Tests.Draft.SourceControl;

public sealed class ProjectPullRequestRoutingTests
{
    [Fact]
    public async Task A_sessionless_remote_project_uses_its_original_owner_and_refuses_a_replacement_source()
    {
        await using var hosts = new TwoHosts(forwardCommands: true);
        await using var remoteWork = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        await using var localWork = new BackgroundWork(TimeProvider.System, TextWriter.Null);
        await using var remoteOperations = new GitOperations(remoteWork, hosts.Remote.Topics, TimeProvider.System, hosts.Remote.Fixture.Machine);
        await using var localOperations = new GitOperations(localWork, hosts.Mac.Topics, TimeProvider.System, hosts.Mac.Fixture.Machine);
        using var repo = new GitHistoryTestRepository();
        await repo.InitializeAsync();
        await repo.Run("remote", "add", "origin", "https://github.com/owner/repo");
        var remoteGh = new HostedReads(); var desktopGh = new HostedReads();
        ICallRegistry? desktopCalls = null;
        hosts.Mac.AdditionalFeatures = calls => desktopCalls = calls;
        hosts.Remote.AdditionalFeatures = calls =>
        {
            var reader = new PullRequestReader(repo.Git, remoteGh, repo.Root, new GitTextPatch(repo.Git));
            var sources = new SessionRowSources(hosts.Remote.Fixture.Db, hosts.Remote.Sessions,
                new MachineSet(new FakeMachine(null, hosts.Remote.Sessions)));
            var roots = new ProjectRoots(new ProjectStore(hosts.Remote.Fixture.Db, TimeProvider.System));
            new PullRequestsFeature(hosts.Remote.Sessions, reader, new(hosts.Remote.Sessions, hosts.Remote.Fixture.Db, remoteOperations, null, null),
                new(hosts.Remote.Fixture.Db, roots, sources, hosts.Remote.Fixture.Machine, reader, null, null)).Register(calls);
        };
        await hosts.Start(); await hosts.StartMirror();
        var store = new ProjectStore(hosts.Mac.Fixture.Db, TimeProvider.System);
        var project = hosts.Mac.Fixture.Db.Writer.Write("fixture.project", scope => store.AddOrRestore(scope,
            "workbox-test", repo.Root, "Remote project", "github.com/owner/repo").Project);
        var readerLocal = new PullRequestReader(repo.Git, desktopGh, repo.Root, new GitTextPatch(repo.Git));
        var sourcesLocal = new SessionRowSources(hosts.Mac.Fixture.Db, hosts.Mac.Sessions,
            new MachineSet(new FakeMachine(null, hosts.Mac.Sessions)), hosts.Connections);
        var projectReads = new ProjectPullRequestReads(hosts.Mac.Fixture.Db, new ProjectRoots(store), sourcesLocal,
            hosts.Mac.Fixture.Machine, readerLocal, hosts.Mac.Forwarder, hosts.Connections);
        new PullRequestsFeature(hosts.Mac.Sessions, readerLocal, new(hosts.Mac.Sessions, hosts.Mac.Fixture.Db, localOperations, hosts.Mac.Forwarder, hosts.Connections), projectReads).Register(desktopCalls!);
        var source = hosts.Mac.Fixture.Db.QueryFirst("SELECT machine,generation,name FROM engine_mirror WHERE state='live' AND name='workbox-test'",
            row => new ExpectedSource(row.Text(0), row.Long(1), row.Text(2)), default)!;
        using var socket = await ApiTests.OpenOwnerSocketAsync(hosts.Mac.Address, "isolated-token", "/socket/bulk");
        var args = new PullRequestsFeature.DetailArgs(null, 7, source, project.Id);
        var result = ApiTests.Result(await ApiTests.SocketRequestAsync(socket, 1, "git", "pr", JsonSerializer.Serialize(args, PullRequestCallsJson.Default.DetailArgs)));
        Assert.Equal("owner/repo", result.GetProperty("repository").GetString());
        Assert.Equal(repo.Root, result.GetProperty("localRoot").GetString());
        Assert.Equal(0, desktopGh.Reads);
        Assert.Equal(3, remoteGh.Reads);
        Assert.All(remoteGh.Roots, root => Assert.Equal(repo.Root, root));
        var refused = await ApiTests.SocketRequestAsync(socket, 2, "git", "pr", JsonSerializer.Serialize(args with { ExpectedSource = source with { Generation = source.Generation + 1 } }, PullRequestCallsJson.Default.DetailArgs));
        Assert.Equal("source_changed", refused.GetProperty("code").GetString());
        Assert.Equal(3, remoteGh.Reads);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remoteGh.BeforeRead = async () => { remoteGh.BeforeRead = null; entered.TrySetResult(); await release.Task; };
        var pending = ApiTests.SocketRequestAsync(socket, 3, "git", "pr", JsonSerializer.Serialize(args, PullRequestCallsJson.Default.DetailArgs));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        hosts.Mac.Fixture.Db.Writer.Write("fixture.remove-project", scope => store.Remove(scope, project.Id, false));
        release.TrySetResult();
        var removed = await pending;
        Assert.Equal("source_changed", removed.GetProperty("code").GetString());
        Assert.Equal(6, remoteGh.Reads);
        Assert.Equal(0, desktopGh.Reads);
    }

    private sealed class HostedReads : IGh
    {
        internal int Reads;
        internal Func<Task>? BeforeRead;
        internal readonly List<string> Roots = [];
        public async Task<GhRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan limit, CancellationToken ct)
        {
            Reads++; Roots.Add(folder);
            if (BeforeRead is { } before) await before();
            return new GhRun(0, args[0] == "pr" ? """{"number":7,"title":"Remote PR","headRefOid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseRefOid":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"OPEN"}""" : "[]", "");
        }
    }
}
