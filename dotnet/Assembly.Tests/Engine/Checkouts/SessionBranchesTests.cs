using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Common.Topics;
using Assembly.Domain.Rows;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine;
using Assembly.Infrastructure.Engine.Checkouts;
using Assembly.Infrastructure.Git;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Checkouts;

public sealed class SessionBranchesTests
{
    [Fact]
    public async Task A_finished_tool_call_after_a_model_switch_moves_the_record_and_pushes_after_commit()
    {
        await using var f = await Fixture.Create();
        await f.Git("switch", "-c", "model-branch");
        var version = f.Engine.Number("SELECT version FROM engine_session WHERE id='s'");

        f.Branches.RowsCommitted("s", [Row(RowKind.Tool, new { state = "completed", kind = "execute" })]);
        await f.Pushed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("model-branch", f.Engine.Text("SELECT branch FROM engine_session WHERE id='s'"));
        Assert.Equal(version + 1, f.Engine.Number("SELECT version FROM engine_session WHERE id='s'"));
        Assert.Equal("model-branch", f.Sessions.Get("s")!.Branch);
        Assert.Equal(1, f.Engine.Number("SELECT COUNT(*) FROM engine_event WHERE kind='session.checkout_changed' AND entity_id='s'"));
    }

    [Fact]
    public async Task A_switch_from_a_viewed_folder_moves_every_session_recorded_there()
    {
        await using var f = await Fixture.Create();
        await f.Git("switch", "-c", "view-branch");

        f.Branches.FolderChanged(f.Root);
        await f.Pushed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("view-branch", f.Sessions.Get("s")!.Branch);
    }

    [Fact]
    public async Task A_detached_head_has_no_branch()
    {
        await using var f = await Fixture.Create();
        await f.Git("switch", "--detach");

        f.Branches.FolderChanged(f.Root);
        await f.Pushed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(f.Engine.Text("SELECT branch FROM engine_session WHERE id='s'"));
    }

    [Theory]
    [InlineData(RowKind.Tool, "started", "execute")]
    [InlineData(RowKind.Tool, "completed", "read")]
    [InlineData(RowKind.Tool, "completed", "search")]
    [InlineData(RowKind.Turn, "started", null)]
    [InlineData(RowKind.Message, "completed", null)]
    public async Task A_row_that_cannot_have_moved_the_branch_runs_no_git(string kind, string state, string? toolKind)
    {
        await using var f = await Fixture.Create();
        f.Branches.RowsCommitted("s", [Row(kind, new { state, kind = toolKind })]);
        await f.Branches.DisposeAsync();
        Assert.Equal(0, f.Counting.Reads);
    }

    [Theory]
    [InlineData(RowKind.Tool, "failed", "edit")]
    [InlineData(RowKind.Tool, "completed", null)]
    [InlineData(RowKind.Turn, "finished", null)]
    public async Task A_row_that_can_have_moved_the_branch_asks_git(string kind, string state, string? toolKind)
    {
        await using var f = await Fixture.Create();
        f.Branches.RowsCommitted("s", [Row(kind, new { state, kind = toolKind })]);
        await f.Counting.Read.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, f.Counting.Reads);
    }

    [Fact]
    public async Task A_checkout_the_engine_has_underway_settles_the_record_itself()
    {
        await using var f = await Fixture.Create();
        f.Engine.Db.Writer.Write("test.pending", scope => scope.Execute("UPDATE engine_session SET checkout_state='pending' WHERE id='s'"));
        await f.Git("switch", "-c", "other");

        f.Branches.FolderChanged(f.Root);
        await f.Counting.Read.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await f.Branches.DisposeAsync();

        Assert.Equal("main", f.Engine.Text("SELECT branch FROM engine_session WHERE id='s'"));
    }

    [Fact]
    public async Task A_folder_that_stopped_being_a_repository_keeps_the_record()
    {
        await using var f = await Fixture.Create();
        Directory.Delete(Path.Combine(f.Root, ".git"), true);

        f.Branches.FolderChanged(f.Root);
        await f.Counting.Read.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await f.Branches.DisposeAsync();

        Assert.Equal("main", f.Engine.Text("SELECT branch FROM engine_session WHERE id='s'"));
    }

    [Fact]
    public async Task Stopping_leaves_nothing_running_and_a_later_change_starts_nothing()
    {
        await using var f = await Fixture.Create();
        await f.Branches.DisposeAsync();
        f.Branches.FolderChanged(f.Root);
        Assert.Equal(0, f.Counting.Reads);
    }

    [Fact]
    public void Only_the_system_reports_a_branch_and_the_same_one_changes_nothing()
    {
        using var f = new EngineFixture();
        f.SeedSession();
        var system = new EngineActor(ActorKind.System, "system");
        Assert.Equal("forbidden", f.Apply(new ReportSessionBranch("x"), "s").Error);
        Assert.Equal("forbidden", f.Apply(new ReportSessionBranch("x"), "s", actor: new(ActorKind.Session, "s")).Error);
        Assert.Equal("bad_args", f.Apply(new ReportSessionBranch(" "), "s", actor: system).Error);
        Assert.Equal("bad_args", f.Apply(new ReportSessionBranch("a\nb"), "s", actor: system).Error);
        Assert.Equal("bad_args", f.Apply(new ReportSessionBranch(new string('a', 257)), "s", actor: system).Error);

        Assert.True(f.Apply(new ReportSessionBranch("x"), "s", actor: system).Applied);
        Assert.Equal("x", f.Text("SELECT branch FROM engine_session WHERE id='s'"));
        Assert.Equal(1, f.Number("SELECT version FROM engine_session WHERE id='s'"));

        Assert.True(f.Apply(new ReportSessionBranch("x"), "s", version: 1, actor: system).Applied);
        Assert.Equal(1, f.Number("SELECT version FROM engine_session WHERE id='s'"));

        Assert.True(f.Apply(new ReportSessionBranch(null), "s", version: 1, actor: system).Applied);
        Assert.Null(f.Text("SELECT branch FROM engine_session WHERE id='s'"));
    }

    private static StoredRow Row(string kind, object payload) =>
        new("s", 1, "run", "item", kind, JsonSerializer.SerializeToElement(payload), 1);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PushTopics _topics = new();
        private IAsyncDisposable _runtime = null!;
        internal EngineFixture Engine = null!;
        internal SessionStore Sessions = null!;
        internal SessionBranches Branches = null!;
        internal readonly CountingGit Counting = new();
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "session-branches-" + Guid.NewGuid().ToString("N"));
        internal TaskCompletionSource Pushed => _topics.Pushed;

        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Root);
            await f.Git("init", "--initial-branch=main");
            await f.Git("config", "user.name", "Session branches fixture");
            await f.Git("config", "user.email", "session-branches@example.invalid");
            await f.Git("commit", "--allow-empty", "-m", "initial");
            f.Engine = new EngineFixture(topics: f._topics);
            f.Sessions = SessionStore.Open(f.Engine.Db, f.Engine.Time, f.Root);
            f.Sessions.Add("s", "codex", "", null!, workingFolder: f.Root);
            f.Engine.SeedSession();
            f.Engine.Db.Writer.Write("test.folder", scope => scope.Execute("UPDATE engine_session SET folder=$root,branch='main' WHERE id='s'", new SqlArg("$root", f.Root)));
            var cutover = new SessionCutover(f.Engine.Db, f.Sessions, new FakeLauncher(), f.Root, f.Engine.Time, _ => null);
            f._runtime = EngineRuntime.Start(f.Engine.Db, new Assembly.Infrastructure.Topics.Topics(), f.Engine.Time, f.Engine.Limits, cutover: cutover);
            f.Branches = new SessionBranches(cutover, f.Counting, TextWriter.Null);
            f._topics.Listening = true;
            return f;
        }

        internal async Task Git(params string[] args) =>
            Assert.Equal(0, (await new GitCli().RunAsync(Root, args, null, null, default)).ExitCode);

        public async ValueTask DisposeAsync()
        {
            await Branches.DisposeAsync();
            await _runtime.DisposeAsync();
            Engine.Dispose();
            Directory.Delete(Root, true);
        }
    }

    private sealed class CountingGit : IGit
    {
        private readonly GitCli _git = new();
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        internal TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, string? stdin, TimeSpan? limit, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            var run = await _git.RunAsync(folder, arguments, stdin, limit, cancellationToken);
            Read.TrySetResult();
            return run;
        }

        public IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> arguments, IReadOnlyList<string>? input, CancellationToken cancellationToken) =>
            _git.ReadAsync(folder, arguments, input, cancellationToken);
    }

    private sealed class PushTopics : ITopics
    {
        internal bool Listening;
        internal TaskCompletionSource Pushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Publish(string topic, string? key, string json, bool keyedOnly = false)
        {
            if (Listening && topic == SessionsFeature.RailTopic && key == "s") Pushed.TrySetResult();
        }

        public bool HasSubscribers(string topic, string key) => true;
        public event Action<string, string?>? Unsubscribed { add { } remove { } }
    }
}
