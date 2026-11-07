using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.GitHub;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.SourceControl.PullRequests;
using Assembly.Infrastructure.Chat.SourceControl;
using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Persistence;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Tests.Draft.SourceControl;

public sealed partial class GitSessionOperationsTests
{
    [Fact]
    public async Task A_lost_pr_write_preserves_actual_host_machine_and_never_replays()
    {
        await using var f = await Fixture.Create();
        var execute = f.Producer.Prepare(f.Request());
        var failure = await Assert.ThrowsAsync<CallUncertain>(() => execute(default));
        Assert.Equal(f.Machine, failure.Machine);
        Assert.Equal("This may have posted. Refresh before trying again.", failure.Message);
        Assert.Equal(1, f.Gh.Writes);
    }
    [Fact]
    public async Task A_changed_checkout_is_refused_before_any_github_read_or_write()
    {
        await using var f = await Fixture.Create();
        var execute = f.Producer.Prepare(f.Request());
        var other = Directory.CreateDirectory(Path.Combine(f.Repo.Root, "replacement")).FullName;
        f.Db.Writer.Write("change.checkout", scope => scope.Execute("UPDATE session_info SET working_folder=$root WHERE session_id=$id",
            new SqlArg("$root", other), new SqlArg("$id", f.Session)));
        Assert.Equal(other, f.Sessions.Root(f.Sessions.Get(f.Session)!));
        await Assert.ThrowsAsync<CallFailed>(() => execute(default));
        Assert.Equal(0, f.Gh.Reads); Assert.Equal(0, f.Gh.Writes);
    }
    [Fact]
    public async Task Unsupported_actions_and_missing_sessions_cannot_open_git_or_github()
    {
        await using var f = await Fixture.Create();
        Assert.Throws<CallFailed>(() => f.Producer.Prepare(f.Request() with { Action = "arbitrary" }));
        Assert.Throws<CallFailed>(() => f.Producer.Prepare(f.Request() with { Session = "missing" }));
        Assert.Equal(0, f.Gh.Reads); Assert.Equal(0, f.Gh.Writes);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public GitHistoryTestRepository Repo { get; } = new();
        private readonly TempDatabase file = new();
        private readonly BackgroundWork work = new(TimeProvider.System, TextWriter.Null);
        public AppDatabase Db { get; private set; } = null!;
        public string Machine { get; private set; } = null!;
        public SessionStore Sessions { get; private set; } = null!;
        public string Session { get; private set; } = null!;
        public LostGh Gh { get; } = new();
        private GitOperations operations = null!;
        public GitSessionOperations Producer { get; private set; } = null!;
        public GitOperationStart Request() => new(operations.InstanceId, Guid.NewGuid(), Session, "comment",
            JsonSerializer.SerializeToElement(new { number = 7, expectedHead = new string('a', 40), body = "Comment" }));
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); await f.Repo.InitializeAsync();
            await f.Repo.Run("remote", "add", "origin", "https://github.com/owner/repo");
            var topics = new TopicSet(); f.Db = AppDatabase.Open(f.file.Path, TimeProvider.System, topics);
            f.Machine = Assembly.Engine.Persistence.EngineSchema.Open(f.Db, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
            f.Sessions = SessionStore.Open(f.Db, TimeProvider.System, f.Repo.Root);
            f.Session = f.Sessions.Create("claude", "https://github.com/owner/repo", workingFolder: f.Repo.Root).SessionId;
            f.operations = new(f.work, topics, TimeProvider.System, f.Machine);
            var prReader = new PullRequestReader(f.Repo.Git, f.Gh, f.Repo.Root, new GitTextPatch(f.Repo.Git));
            var utilityPreferences = new Assembly.Application.Features.Preferences.PreferenceStore(f.Db);
            var utility = new Assembly.Infrastructure.Agents.AcpUtilityModel([], () => utilityPreferences.Read("utilityModel"), TimeProvider.System, TextWriter.Null);
            f.Producer = new(f.Sessions, f.Repo.Git, new(f.Repo.Git, f.Gh, prReader), new(), f.operations, new(f.Repo.Git, utility), prReader, f.Db);
            return f;
        }
        public async ValueTask DisposeAsync() { await operations.DisposeAsync(); await work.DisposeAsync(); Db.Dispose(); file.Dispose(); Repo.Dispose(); }
    }
    private sealed class LostGh : IGh
    {
        public int Reads, Writes;
        internal Action? OnRead;
        public Task<GhRun> RunAsync(string root, IReadOnlyList<string> args, string? input, TimeSpan limit, CancellationToken ct)
        {
            if (args.Contains("POST")) { Writes++; throw new GhInterrupted("lost", new IOException("fixture"), ct); }
            Reads++; OnRead?.Invoke();
            return Task.FromResult(new GhRun(0, args[0] == "pr" ? """{"number":7,"state":"OPEN","headRefOid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""" : args[1] == "graphql" ? """{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[]}}}}}""" : "[]", ""));
        }
    }
}
