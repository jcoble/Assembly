using System.Diagnostics;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Agents;
using Assembly.Infrastructure.Chat;
using Assembly.Infrastructure.Engine;
using Assembly.Tests.Features.Chat.Sessions;
using Topics = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Engine;

public sealed class SessionLocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_Git_and_nonGit_managed_shaped_folders_survive_restart_and_reach_new_resume_files_and_terminal(bool git)
    {
        await using var h = new Host(managed: false, git);
        await h.Restart();
        var partial = Directory.CreateDirectory(h.Folder + ".partial").FullName;
        File.WriteAllText(Path.Combine(partial, "keep"), "unrelated user data");
        AssertLocation(h, "root", h.Folder, false);
        var opening = h.Opener.OpenAsync("root", "First", new(h.F.Time), CancellationToken.None);
        var agent = await h.Launcher.Fake.NextAsync();
        var first = await agent.StartAsync();
        Assert.Equal(h.Folder, first.GetProperty("params").GetProperty("cwd").GetString());
        var opened = await opening;
        await h.Close(opened, agent);
        var resuming = h.Opener.OpenAsync("root", "Again", new(h.F.Time), CancellationToken.None);
        var resume = await agent.OpenAsync();
        Assert.Equal("session/resume", resume.GetProperty("method").GetString());
        Assert.Equal(h.Folder, resume.GetProperty("params").GetProperty("cwd").GetString());
        await h.Close(await resuming, agent);
        Assert.Equal(0, h.Launcher.Checkouts);
        Assert.Equal("draft", File.ReadAllText(Path.Combine(h.Folder, "notes.md")));
        Assert.Equal("unrelated user data", File.ReadAllText(Path.Combine(partial, "keep")));
    }

    [Fact]
    public async Task Identical_repo_and_folder_strings_in_managed_mode_retain_actual_clone_failure_after_restart()
    {
        await using var h = new Host(managed: true, git: false);
        await h.Restart(); AssertLocation(h, "root", h.Folder, true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Opener.OpenAsync("root", "First", new(h.F.Time), CancellationToken.None));
        Assert.Contains("git clone", error.Message);
        Assert.Equal(1, h.Launcher.Checkouts); Assert.Equal(0, h.Launcher.Fake.Launches);
        Assert.Equal("draft", File.ReadAllText(Path.Combine(h.Folder, "notes.md")));
        Assert.False(Directory.Exists(h.Folder + ".partial"));
    }

    [Fact]
    public async Task Missing_direct_and_contradictory_managed_folders_fail_before_provider_without_clone_or_retarget()
    {
        await using var h = new Host(managed: false, git: false);
        Directory.Delete(h.Folder, true);
        var missing = await Assert.ThrowsAsync<CallFailed>(() => h.Opener.OpenAsync("root", "First", new(h.F.Time), CancellationToken.None));
        Assert.Contains(h.Folder, missing.Message); Assert.False(Directory.Exists(h.Folder));
        h.F.Db.Writer.Write("test.contradictory", scope => scope.Execute("UPDATE engine_session SET managed_checkout=1,folder=$folder WHERE id='root'", new Assembly.Application.Common.Database.SqlArg("$folder", Path.Combine(h.Home, "different"))));
        var contradiction = await Assert.ThrowsAsync<CallFailed>(() => h.Opener.OpenAsync("root", "First", new(h.F.Time), CancellationToken.None));
        Assert.Contains("managed checkout", contradiction.Message);
        Assert.Equal(0, h.Launcher.Checkouts); Assert.Equal(0, h.Launcher.Fake.Launches);
    }

    [Fact]
    public async Task Native_child_inherits_its_nearest_owning_helpers_explicit_location_and_foreign_stays_foreign()
    {
        await using var h = new Host(managed: false, git: false);
        var childFolder = Directory.CreateDirectory(Path.Combine(h.Home, "child-location")).FullName;
        var context = new CommandContext(Guid.NewGuid(), h.Runtime.Machine, new(ActorKind.Window, "owner"), "helper", 0, 0, 1000);
        Assert.True(h.Runtime.Submit(context, new StartSession("claude", null, childFolder, "Helper", "root", null, null, ManagedCheckout: false)).Applied);
        var native = h.Store.Child(h.Store.Get("helper")!, "native-acp", "Native");
        AssertLocation(h, native.SessionId, childFolder, false);
        var foreign = h.Store.Add("peer", "claude", "repo", "foreign-machine");
        Assert.Null(h.Store.Root(foreign));
        Assert.Throws<CallFailed>(() => SessionPaths.RootOf(h.Store, foreign.SessionId));
        Assert.Throws<CallFailed>(() => AdapterLauncher.ExistingFolder(h.Checkouts, h.Store)(foreign.SessionId));
    }

    private static void AssertLocation(Host h, string id, string folder, bool managed)
    {
        var session = h.Store.Get(id)!;
        Assert.Equal(folder, h.Store.Location(session)!.Folder); Assert.Equal(managed, h.Store.Location(session)!.ManagedCheckout);
        Assert.Equal(folder, h.Store.Root(session)); Assert.Equal(folder, h.Store.Item(session).Root);
        Assert.Equal(folder, SessionPaths.RootOf(h.Store, id)); Assert.Equal(folder, AdapterLauncher.ExistingFolder(h.Checkouts, h.Store)(id));
    }

    private sealed class Host : IAsyncDisposable
    {
        public string Home { get; } = Directory.CreateTempSubdirectory("assembly-location-").FullName;
        private readonly Topics _topics = new();
        public EngineFixture F { get; }
        public SessionStore Store { get; private set; }
        public EngineRuntime Runtime { get; private set; }
        public TrackingLauncher Launcher { get; }
        public SharedAdapter Adapter { get; }
        public SessionOpener Opener { get; private set; }
        public string Checkouts => Path.Combine(Home, "checkouts");
        public string Folder => Path.Combine(Checkouts, "root");
        public Host(bool managed, bool git)
        {
            Directory.CreateDirectory(Folder); File.WriteAllText(Path.Combine(Folder, "notes.md"), "draft");
            if (git) Git(Folder, "init", "--quiet");
            F = new(topics: _topics); Launcher = new(Checkouts);
            Assert.True(F.Apply(new StartSession("claude", null, Folder, "Root", null, null, null, ManagedCheckout: managed), "root", actor: new(ActorKind.Window, "owner")).Applied);
            Store = SessionStore.Open(F.Db, F.Time, Checkouts);
            Runtime = EngineRuntime.Start(F.Db, _topics, F.Time, F.Limits, cutover: new(F.Db, Store, Launcher, Checkouts, F.Time));
            Adapter = new(Launcher, "claude", F.Time, TextWriter.Null); Opener = new(Store, Launcher, _ => Adapter);
            Assert.Equal(Folder, Store.Get("root")!.RepoUrl);
        }
        public async Task Restart()
        {
            await Runtime.DisposeAsync(); Store = SessionStore.Open(F.Db, F.Time, Checkouts);
            Runtime = EngineRuntime.Start(F.Db, _topics, F.Time, F.Limits, cutover: new(F.Db, Store, Launcher, Checkouts, F.Time));
            Opener = new(Store, Launcher, _ => Adapter);
        }
        public async Task Close(OpenSession session, FakeLink agent)
        {
            var closing = Adapter.CloseAsync(session.Adapter, session.AcpSessionId); await agent.CloseAsync(); await closing;
            await Adapter.ReleaseAsync(session.Adapter);
        }
        public async ValueTask DisposeAsync() { await Adapter.DisposeAsync(); await Runtime.DisposeAsync(); F.Dispose(); Directory.Delete(Home, true); }
    }

    private sealed class TrackingLauncher(string checkouts) : IAgentLauncher
    {
        public FakeLauncher Fake { get; } = new(); public int Checkouts;
        public IReadOnlyList<AgentDescriptor> Agents => Fake.Agents;
        public Task<string> CheckoutAsync(SessionInfo session, StageTimes stages, CancellationToken cancellationToken)
        {
            Checkouts++;
            return new AdapterLauncher([], checkouts, new UnclaimedPermissions()).CheckoutAsync(session, stages, cancellationToken);
        }
        public void ValidateFolder(string folder) => new AdapterLauncher([], checkouts, new UnclaimedPermissions()).ValidateFolder(folder);
        public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken) => Fake.LaunchAsync(agentId, stages, cancellationToken);
    }
    private static void Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var git = Process.Start(start)!; git.WaitForExit(); Assert.Equal(0, git.ExitCode);
    }
}
