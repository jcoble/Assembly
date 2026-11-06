using Assembly.Application.Common;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Git;
using Assembly.Application.Common.Projects;
using Assembly.Engine;
using Assembly.Engine.Persistence;
using System.Text.Json;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Checkouts;
using Assembly.Infrastructure.Git;
using Assembly.Tests.Features.Chat.Sessions;

namespace Assembly.Tests.Engine.Checkouts;

public sealed class ExistingCheckoutTests
{
    [Fact]
    public async Task In_place_branch_commits_actual_git_and_preserves_session_and_provider_identity()
    {
        await using var f = await Fixture.Create();
        var effect = f.Queue(new("branch", "feature", null, null, true, f.Head));
        Assert.Equal("main", (await f.Git.RunAsync(f.Root, ["branch", "--show-current"], null, null, default)).Output.Trim());
        var receipt = await f.Effects.ExecuteAsync(effect, default);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        Assert.True(CheckoutTransitionTests.Complete(f.Db, effect, receipt, f.Sessions).Applied);
        Assert.Equal("feature", f.Sessions.Get("s")!.Branch);
        Assert.Equal(f.Root, f.Db.Text("SELECT folder FROM engine_session WHERE id='s'"));
        Assert.Equal("opaque-existing-public-id", f.Db.Text("SELECT provider_session_id FROM engine_session WHERE id='s'"));
        Assert.Equal("project-original", f.Db.Text("SELECT project_id FROM engine_session WHERE id='s'"));
        Assert.Empty(f.Launcher.Launched);
    }

    [Fact]
    public async Task Conflicting_changes_refuse_without_stash_force_or_projection_change()
    {
        await using var f = await Fixture.Create();
        await f.Run("switch", "-c", "feature"); await File.WriteAllTextAsync(Path.Combine(f.Root, "file"), "feature");
        await f.Run("commit", "-am", "feature"); await f.Run("switch", "main");
        await File.WriteAllTextAsync(Path.Combine(f.Root, "file"), "uncommitted-owner-work");
        var effect = f.Queue(new("branch", "feature", null, null, ExpectedHead: f.Head));
        var receipt = await f.Effects.ExecuteAsync(effect, default);
        Assert.True(CheckoutTransitionTests.Complete(f.Db, effect, receipt, f.Sessions).Applied);
        Assert.Equal("refused", f.Db.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        Assert.Equal("uncommitted-owner-work", await File.ReadAllTextAsync(Path.Combine(f.Root, "file")));
        Assert.Null(f.Sessions.Get("s")!.Branch); Assert.Empty(f.Launcher.Launched);
    }

    [Fact]
    public async Task Interrupted_confirmed_git_is_inspected_once_without_replaying_switch()
    {
        await using var f = await Fixture.Create();
        var effect = f.Queue(new("branch", "feature", null, null, true, f.Head));
        Assert.Equal(EffectDisposition.Confirmed, (await f.Effects.ExecuteAsync(effect, default)).Disposition);
        var switches = f.Git.Switches;
        Assert.True(f.Db.Apply(CheckoutTransitionTests.Context(f.Db, effect.Id, 1), new RecoverEffect()).Applied);
        Assert.True(f.Db.Apply(CheckoutTransitionTests.Context(f.Db, effect.Id, 2), new ClaimEffect()).Applied);
        var retried = EffectTransitions.Read(f.Db.Db, effect.Id, default)!;
        var receipt = await f.Effects.ReconcileAsync(retried, default);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition); Assert.Equal(switches, f.Git.Switches);
        Assert.True(f.Db.Db.Writer.Write("test.complete-recovered", scope => new Assembly.Engine.EngineCommands(f.Db.Machine, f.Db.Limits, projectProjection: f.Sessions)
            .Apply(scope, CheckoutTransitionTests.Context(f.Db, effect.Id, 3), new CompleteEffect(receipt))).Applied);
        Assert.Equal("feature", f.Sessions.Get("s")!.Branch); Assert.Empty(f.Launcher.Launched);
    }

    [Fact]
    public async Task Missing_verified_native_resume_refuses_folder_move_and_releases_actual_maintenance_lease()
    {
        await using var f = await Fixture.Create(); var other = Path.Combine(f.Root, "other"); Directory.CreateDirectory(other);
        var effect = f.Queue(new("folder", null, null, other));
        var receipt = await f.Effects.ExecuteAsync(effect, default);
        Assert.True(CheckoutTransitionTests.Complete(f.Db, effect, receipt, f.Sessions).Applied);
        Assert.Equal(f.Root, f.Db.Text("SELECT folder FROM engine_session WHERE id='s'"));
        Assert.Empty(f.Launcher.Launched);
        var first = f.Hosts.TryAcquire("s"); Assert.NotNull(first.Lease);
        Assert.Null(f.Hosts.TryAcquire("s").Lease);
        Assert.False(await first.Lease!.ProbeResumeAsync("opaque-existing-public-id", other, default));
        await first.Lease.DisposeAsync();
        var later = f.Hosts.TryAcquire("s"); Assert.NotNull(later.Lease); await later.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task Initial_new_worktree_uses_only_owned_root_and_defers_exact_start_until_git_confirms()
    {
        await using var f = await Fixture.Create(); Directory.CreateDirectory(f.WorktreeRoot);
        var effect = f.QueueInitial(new("worktree", null, "main"));
        Assert.Equal(0, f.Db.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        var receipt = await f.Effects.ExecuteAsync(effect, default);
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        Assert.True(CheckoutTransitionTests.Complete(f.Db, effect, receipt, f.Sessions).Applied);
        var target = f.Db.Text("SELECT folder FROM engine_session WHERE id='s'")!;
        Assert.StartsWith(f.WorktreeRoot + Path.DirectorySeparatorChar, target);
        Assert.Equal("initial", await File.ReadAllTextAsync(Path.Combine(target, "file")));
        Assert.Equal("initial", await File.ReadAllTextAsync(Path.Combine(f.Root, "file")));
        Assert.Equal(target, f.Sessions.Get("s")!.CheckoutRoot);
        Assert.Equal(1, f.Db.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        Assert.Empty(f.Launcher.Launched);
    }

    [Fact]
    public async Task Initial_held_destination_refuses_actual_busy_peer_then_retries_same_session_once()
    {
        await using var f = await Fixture.Create(); Directory.CreateDirectory(f.WorktreeRoot);
        var target = Path.Combine(f.WorktreeRoot, "held"); await f.Run("worktree", "add", "-b", "feature", target, "HEAD");
        f.Db.SeedSession("peer", "running");
        f.Db.Db.Writer.Write("test.target-peer", scope => scope.Execute("UPDATE engine_session SET folder=$target WHERE id='peer'", new SqlArg("$target", target)));
        f.Sessions.Add("peer", "codex", "", null!, workingFolder: target);
        var first = f.QueueInitial(new("branch", "feature", null));
        var original = first.Payload.Deserialize(EngineJson.Default.CheckoutOperation)!.InitialOperationId;
        Assert.NotNull(original);
        Assert.True(CheckoutTransitionTests.Complete(f.Db, first, await f.Effects.ExecuteAsync(first, default), f.Sessions).Applied);
        Assert.Equal("refused", f.Db.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        var shown = new Assembly.Engine.Reading.EngineReader(f.Db.Db, f.Db.Machine).Entity("session", "s", default)!.Value;
        Assert.Equal("checkout_busy", shown.GetProperty("checkoutReason").GetString());
        Assert.Equal(f.Root, f.Db.Text("SELECT folder FROM engine_session WHERE id='s'"));
        Assert.Equal(0, f.Db.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        f.Db.Db.Writer.Write("test.peer-ended", scope => scope.Execute("UPDATE engine_session SET state='ended' WHERE id='peer'"));
        var retry = f.Queue(new("branch", "feature", null, null));
        var retryIntent = retry.Payload.Deserialize(EngineJson.Default.CheckoutOperation)!;
        Assert.Equal(original, retryIntent.InitialOperationId); Assert.NotEqual(original, retryIntent.OperationId);
        Assert.True(CheckoutTransitionTests.Complete(f.Db, retry, await f.Effects.ExecuteAsync(retry, default), f.Sessions).Applied);
        Assert.Equal(target, f.Db.Text("SELECT folder FROM engine_session WHERE id='s'"));
        Assert.Equal(1, f.Db.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        var startId = f.Db.Text("SELECT id FROM engine_outbox WHERE kind='start'")!;
        Assert.True(f.Db.Apply(CheckoutTransitionTests.Context(f.Db, startId, 0), new ClaimEffect()).Applied);
        var started = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(
            new SessionEffectData("idle", null, null), EngineJson.Default.SessionEffectData), null);
        Assert.True(CheckoutTransitionTests.Complete(f.Db, EffectTransitions.Read(f.Db.Db, startId, default)!, started, f.Sessions).Applied);
        var ordinary = f.Queue(new("branch", "later", null, null, true, f.Head));
        var ordinaryIntent = ordinary.Payload.Deserialize(EngineJson.Default.CheckoutOperation)!;
        Assert.Equal(original, ordinaryIntent.InitialOperationId); Assert.Null(ordinaryIntent.InitialStart);
        Assert.True(CheckoutTransitionTests.Complete(f.Db, ordinary, await f.Effects.ExecuteAsync(ordinary, default), f.Sessions).Applied);
        Assert.Equal(1, f.Db.Number("SELECT COUNT(*) FROM engine_outbox WHERE kind='start'"));
        Assert.Empty(f.Launcher.Launched);
    }

    private sealed class CountingGit : IGit
    {
        private readonly GitCli _actual = new(); internal int Switches;
        public Task<GitRun> RunAsync(string folder, IReadOnlyList<string> args, string? input, TimeSpan? limit, CancellationToken ct)
        { if (args[0] == "switch") Switches++; return _actual.RunAsync(folder, args, input, limit, ct); }
        public IAsyncEnumerable<string> ReadAsync(string folder, IReadOnlyList<string> args, IReadOnlyList<string>? input, CancellationToken ct) =>
            _actual.ReadAsync(folder, args, input, ct);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly EngineFixture Db = new();
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "checkout-source-" + Guid.NewGuid().ToString("N"));
        internal readonly CountingGit Git = new(); internal readonly FakeLauncher Launcher = new();
        internal SessionStore Sessions = null!; internal SessionRegistry Hosts = null!; internal CheckoutEffects Effects = null!;
        private BackgroundWork _work = null!; internal string Head = null!;
        internal string WorktreeRoot => Root + "-worktrees";
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            try
            {
                await f.Run("init", "--initial-branch=main"); await f.Run("config", "user.name", "Checkout source fixture");
                await f.Run("config", "user.email", "source-fixture@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(f.Root, "file"), "initial"); await f.Run("add", "file"); await f.Run("commit", "-m", "initial");
                f.Head = (await f.Git.RunAsync(f.Root, ["rev-parse", "HEAD"], null, null, default)).Output.Trim();
                f.Db.SeedSession();
                f.Db.Db.Writer.Write("test.checkout-source", scope => scope.Execute("UPDATE engine_session SET folder=$root,project_id='project-original',provider_session_id='opaque-existing-public-id' WHERE id='s'", new SqlArg("$root", f.Root)));
                f.Sessions = SessionStore.Open(f.Db.Db, f.Db.Time, f.Root); f.Sessions.Add("s", "codex", "", null!, workingFolder: f.Root);
                f._work = new(f.Db.Time, TextWriter.Null);
                f.Hosts = new(new LiveHub(RowStore.Open(f.Db.Db)), f.Sessions, f.Launcher, f.Db.Time, TextWriter.Null, f._work);
                f.Effects = new(f.Db.Db, f.Git, new CheckoutWrites(), f.Hosts, f.WorktreeRoot);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal async Task Run(params string[] args) => Assert.Equal(0, (await Git.RunAsync(Root, args, null, null, default)).ExitCode);
        internal Effect Queue(ChangeCheckout request)
        { var result = Db.Apply(request, "s", version: Db.Number("SELECT version FROM engine_session WHERE id='s'")); Assert.True(result.Applied, result.Error); return CheckoutTransitionTests.Claim(Db); }
        internal Effect QueueInitial(CheckoutIntent intent)
        {
            var context = CheckoutTransitionTests.Context(Db, "s", 0);
            Db.Db.Writer.Write("test.initial-checkout", scope =>
            {
                scope.Execute("UPDATE engine_session SET state='starting',version=1,provider_session_id=NULL WHERE id='s'");
                var start = new SessionStartEffect("codex", "model", Root, "Initial source fixture", null, null, null);
                List<EventDraft> events = [];
                List<EffectDraft> effects = [new("", "start", "s", null, JsonSerializer.SerializeToElement(start, EngineJson.Default.SessionStartEffect))];
                SessionTransitions.QueueInitialCheckout(scope, context, intent, events, effects);
                return EngineJournal.Commit(scope, context, 1, 0, events, effects);
            });
            return CheckoutTransitionTests.Claim(Db);
        }
        public async ValueTask DisposeAsync()
        {
            if (Hosts is not null) await Hosts.DisposeAsync(); if (_work is not null) await _work.DisposeAsync();
            Db.Dispose(); if (Directory.Exists(WorktreeRoot)) Directory.Delete(WorktreeRoot, true);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
