using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Sessions;
using Assembly.Infrastructure.Engine.Checkouts;
using Assembly.Infrastructure.Engine.Handoff;
using Assembly.Infrastructure.Git;
using Assembly.Tests.Engine.Checkouts;

namespace Assembly.Tests.Engine.Handoffs;

public sealed class NativeResumeTests
{
    private const string Native = NativeProvider.NativeId;

    private sealed class Moving : IAsyncDisposable
    {
        internal readonly EngineFixture Db = new();
        internal readonly NativeProvider Provider;
        internal readonly string First = Directory.CreateTempSubdirectory("assembly-native-first-").FullName;
        internal readonly string Second = Directory.CreateTempSubdirectory("assembly-native-second-").FullName;
        internal readonly CheckoutEffects Effects;

        internal Moving(string vendor)
        {
            Provider = new(Db, vendor);
            Db.SeedSession(provider: vendor);
            Db.Db.Writer.Write("test.native-session", scope => scope.Execute(
                "UPDATE engine_session SET folder=$folder,provider_session_id=$native,account_context=$account WHERE id='s'",
                new SqlArg("$folder", First), new("$native", Native), new("$account", Provider.Account)));
            Provider.Sessions.Add("s", vendor, "", null!, workingFolder: First);
            Provider.Sessions.SetAcpSession("s", Native);
            Effects = new(Db.Db, new GitCli(), new CheckoutWrites(), Provider.Hosts, First + "-worktrees");
        }

        internal string Folder => Db.Text("SELECT folder FROM engine_session WHERE id='s'")!;

        internal Effect Queue(ChangeCheckout request)
        {
            var result = Db.Apply(request, "s", version: Db.Number("SELECT version FROM engine_session WHERE id='s'"));
            Assert.True(result.Applied, result.Error);
            return CheckoutTransitionTests.Claim(Db);
        }

        internal void Settle(Effect effect, EffectReceipt receipt)
        {
            var result = CheckoutTransitionTests.Complete(Db, effect, receipt, Provider.Sessions);
            Assert.True(result.Applied, result.Error);
        }

        internal string? Reason() => new Assembly.Engine.Reading.EngineReader(Db.Db, Db.Machine).Entity("session", "s", default)!
            .Value.GetProperty("checkoutReason").GetString();

        internal async Task<JsonElement> MoveAsync(string folder)
        {
            var effect = Queue(new("folder", null, null, folder));
            var running = Effects.ExecuteAsync(effect, default);
            var resumed = await Provider.AnswerResumeAsync();
            Settle(effect, await running);
            return resumed;
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Db.Dispose();
            Directory.Delete(First, true); Directory.Delete(Second, true);
        }
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task Folder_move_resumes_the_same_conversation_and_settles_the_new_folder(string vendor)
    {
        await using var f = new Moving(vendor);
        var original = f.Provider.WriteTranscript(f.First);
        var before = await File.ReadAllTextAsync(original);
        var resumed = await f.MoveAsync(f.Second);
        Assert.Equal(Native, resumed.GetProperty("sessionId").GetString());
        Assert.Equal(f.Second, resumed.GetProperty("cwd").GetString());
        Assert.Equal(f.Second, f.Folder);
        Assert.Equal("ready", f.Db.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        Assert.Equal(Native, f.Db.Text("SELECT provider_session_id FROM engine_session WHERE id='s'"));
        Assert.Equal(f.Second, f.Provider.Sessions.NativeLaunch("s")!.Folder);
        Assert.Equal(f.Second, f.Provider.Sessions.Get("s")!.CheckoutRoot);
        var moved = await File.ReadAllTextAsync(f.Provider.TranscriptPath(f.Second));
        Assert.Contains(f.Second, moved); Assert.DoesNotContain(f.First, moved);
        if (vendor == "claude") Assert.Equal(before, await File.ReadAllTextAsync(original));
        else Assert.Equal(original, f.Provider.TranscriptPath(f.Second));
        Assert.Equal(1, f.Provider.Launcher.Launches);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task Moving_back_keeps_turns_made_in_the_other_folder(string vendor)
    {
        await using var f = new Moving(vendor);
        f.Provider.WriteTranscript(f.First);
        await f.MoveAsync(f.Second);
        await File.AppendAllTextAsync(f.Provider.TranscriptPath(f.Second), NativeProvider.Line(vendor, f.Second, "second folder turn") + "\n");
        await f.MoveAsync(f.First);
        Assert.Equal(f.First, f.Folder);
        var back = await File.ReadAllTextAsync(f.Provider.TranscriptPath(f.First));
        Assert.Contains("second folder turn", back); Assert.DoesNotContain(f.Second, back);
    }

    [Fact]
    public async Task A_different_transcript_in_the_new_folder_refuses_and_is_left_alone()
    {
        await using var f = new Moving("claude");
        f.Provider.WriteTranscript(f.First);
        var other = f.Provider.TranscriptPath(f.Second);
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        await File.WriteAllTextAsync(other, NativeProvider.Line("claude", f.Second, "another history") + "\n");
        var effect = f.Queue(new("folder", null, null, f.Second));
        f.Settle(effect, await f.Effects.ExecuteAsync(effect, default));
        Assert.Equal(f.First, f.Folder);
        Assert.Equal("unsupported_resume", f.Reason());
        Assert.Contains("another history", await File.ReadAllTextAsync(other));
        Assert.Equal(0, f.Provider.Launcher.Launches);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task Refused_resume_restores_the_transcript_and_keeps_the_old_folder(string vendor)
    {
        await using var f = new Moving(vendor);
        var original = f.Provider.WriteTranscript(f.First);
        var before = await File.ReadAllTextAsync(original);
        var effect = f.Queue(new("folder", null, null, f.Second));
        var running = f.Effects.ExecuteAsync(effect, default);
        await f.Provider.RefuseResumeAsync(-32000);
        f.Settle(effect, await running);
        Assert.Equal(f.First, f.Folder);
        Assert.Equal("refused", f.Db.Text("SELECT checkout_state FROM engine_session WHERE id='s'"));
        Assert.Equal("unsupported_resume", f.Reason());
        Assert.Equal(before, await File.ReadAllTextAsync(original));
        if (vendor == "claude") Assert.False(File.Exists(f.Provider.TranscriptPath(f.Second)));
        Assert.Equal(f.First, f.Provider.Sessions.NativeLaunch("s")!.Folder);
    }

    [Fact]
    public async Task Interrupted_codex_move_is_finished_by_the_retry_from_its_mapped_file()
    {
        await using var f = new Moving("codex");
        f.Provider.WriteTranscript(f.First);
        var effect = f.Queue(new("folder", null, null, f.Second));
        var running = f.Effects.ExecuteAsync(effect, default);
        await f.Provider.AnswerResumeAsync();
        Assert.Equal(EffectDisposition.Confirmed, (await running).Disposition);
        // The backend died after the provider file changed and before the receipt committed.
        Assert.Contains(f.Second, await File.ReadAllTextAsync(f.Provider.TranscriptPath(f.First)));
        Assert.True(f.Db.Apply(CheckoutTransitionTests.Context(f.Db, effect.Id, 1), new RecoverEffect()).Applied);
        Assert.True(f.Db.Apply(CheckoutTransitionTests.Context(f.Db, effect.Id, 2), new ClaimEffect()).Applied);
        var retried = EffectTransitions.Read(f.Db.Db, effect.Id, default)!;
        var again = f.Effects.ReconcileAsync(retried, default);
        await f.Provider.AnswerResumeAsync();
        var receipt = await again;
        Assert.Equal(EffectDisposition.Confirmed, receipt.Disposition);
        var settled = f.Db.Db.Writer.Write("test.complete-recovered", scope => new EngineCommands(f.Db.Machine, f.Db.Limits, projectProjection: f.Provider.Sessions)
            .Apply(scope, CheckoutTransitionTests.Context(f.Db, effect.Id, 3), new CompleteEffect(receipt)));
        Assert.True(settled.Applied, settled.Error);
        Assert.Equal(f.Second, f.Folder);
    }

    [Fact]
    public async Task Missing_transcript_refuses_before_any_adapter_starts()
    {
        await using var f = new Moving("claude");
        var effect = f.Queue(new("folder", null, null, f.Second));
        f.Settle(effect, await f.Effects.ExecuteAsync(effect, default));
        Assert.Equal(f.First, f.Folder); Assert.Equal("unsupported_resume", f.Reason());
        Assert.Equal(0, f.Provider.Launcher.Launches);
    }

    [Fact]
    public async Task Busy_session_and_unavailable_folder_stay_real_refusals()
    {
        await using var f = new Moving("claude");
        f.Provider.WriteTranscript(f.First);
        var lease = f.Provider.Hosts.TryAcquire("s").Lease!;
        var busy = f.Queue(new("folder", null, null, f.Second));
        f.Settle(busy, await f.Effects.ExecuteAsync(busy, default));
        Assert.Equal("busy", f.Reason());
        await lease.DisposeAsync();
        var gone = f.Queue(new("folder", null, null, Path.Combine(f.Second, "gone")));
        f.Settle(gone, await f.Effects.ExecuteAsync(gone, default));
        Assert.Equal("folder_unavailable", f.Reason());
        Assert.Equal(f.First, f.Folder); Assert.Equal(0, f.Provider.Launcher.Launches);
    }

    [Fact]
    public async Task Branch_held_by_another_worktree_moves_the_session_there()
    {
        await using var f = new Moving("claude");
        var git = new GitCli();
        async Task Git(params string[] args) => Assert.Equal(0, (await git.RunAsync(f.First, args, null, null, default)).ExitCode);
        await Git("init", "--initial-branch=main"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(f.First, "file"), "x"); await Git("add", "file"); await Git("commit", "-m", "initial");
        var held = f.Second + "-held";
        await Git("worktree", "add", "-b", "feature", held, "HEAD");
        try
        {
            f.Provider.WriteTranscript(f.First);
            var effect = f.Queue(new("branch", "feature", null, null));
            var running = f.Effects.ExecuteAsync(effect, default);
            var resumed = await f.Provider.AnswerResumeAsync();
            f.Settle(effect, await running);
            Assert.Equal(held, resumed.GetProperty("cwd").GetString());
            Assert.Equal(held, f.Folder);
            Assert.Equal("feature", f.Provider.Sessions.Get("s")!.Branch);
        }
        finally { Directory.Delete(held, true); }
    }

    [Fact]
    public async Task A_real_open_freezes_the_launch_a_later_move_must_still_match()
    {
        await using var f = new Moving("claude");
        f.Provider.WriteTranscript(f.First);
        f.Provider.Hosts.For("s").Send("hello");
        var agent = await f.Provider.Launcher.NextAsync();
        await agent.StartAsync();
        await agent.FinishAsync(await agent.ReadAsync("session/prompt"), """{"stopReason":"end_turn"}""");
        await agent.CloseAsync();
        var launch = f.Provider.Sessions.NativeLaunch("s")!;
        Assert.Equal((Native, f.First, f.Provider.Home, f.Provider.Account), (launch.AcpSessionId, launch.Folder, launch.ProviderHome, launch.AccountContext));
        // The account home changed after that open: this transcript no longer belongs to the launch.
        var launches = f.Provider.Launcher.Launches;
        f.Provider.Sessions.RetainNativeLaunch("s", launch with { ProviderHome = launch.ProviderHome + "-other" });
        var effect = f.Queue(new("folder", null, null, f.Second));
        f.Settle(effect, await f.Effects.ExecuteAsync(effect, default));
        Assert.Equal(f.First, f.Folder); Assert.Equal("unsupported_resume", f.Reason());
        Assert.Equal(launches, f.Provider.Launcher.Launches);
    }

    private static HandoffIdentity Identity(EngineFixture target) => new("h", "source-machine", "s", target.Machine, 7, 8);
    private static readonly string Accept = new('a', 64);

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task Adopted_target_resume_is_the_only_source_of_resumed_evidence(string vendor)
    {
        using var target = new EngineFixture();
        await using var provider = new NativeProvider(target, vendor);
        var identity = Identity(target);
        var (start, folder) = await provider.AdoptAsync(identity, Accept);
        Assert.Equal("operation_evidence_required", provider.Ack(identity).Reason);
        Assert.Null(provider.Ack(identity).Target);
        var running = provider.Effects().ExecuteAsync(start, default);
        var resumed = await provider.AnswerResumeAsync();
        Assert.Equal(folder, resumed.GetProperty("cwd").GetString());
        Assert.Null(provider.Ack(identity).Target);
        var completed = provider.Complete(start, await running, 8);
        Assert.True(completed.Applied, completed.Error);
        var evidence = provider.Ack(identity).Target!;
        Assert.Equal(HandoffTargetState.Resumed, evidence.State);
        Assert.Equal("start/h/1", evidence.StartEffectId);
        Assert.Equal(8, evidence.Epoch);
        Assert.Equal("idle", target.State("engine_session", "h"));
        Assert.Equal(Native, target.Text("SELECT provider_session_id FROM engine_session WHERE id='h'"));
        target.Reopen();
    }

    [Fact]
    public async Task A_start_receipt_without_the_adapter_capture_is_refused_and_proves_nothing()
    {
        using var target = new EngineFixture();
        await using var provider = new NativeProvider(target);
        var identity = Identity(target);
        var (start, _) = await provider.AdoptAsync(identity, Accept);
        var forged = new EffectReceipt(EffectDisposition.Confirmed, JsonSerializer.SerializeToElement(
            new SessionEffectData("idle", Native, null), EngineJson.Default.SessionEffectData), null);
        Assert.Equal("bad_receipt", provider.Complete(start, forged, 8).Error);
        Assert.Null(provider.Ack(identity).Target);
        Assert.Equal(0, provider.Launcher.Launches);
    }

    [Fact]
    public async Task A_changed_transcript_never_prepares_an_adoption()
    {
        using var target = new EngineFixture();
        await using var provider = new NativeProvider(target);
        var folder = Directory.CreateTempSubdirectory("assembly-native-target-").FullName;
        provider.WriteTranscript(folder);
        var identity = Identity(target);
        var refused = await Assert.ThrowsAsync<HandoffRefused>(() => new NativeHandoffProducer(provider.Launcher, provider.Sessions).PrepareAsync(target.Machine, new(identity,
            new("claude", "model", folder, "Adopted", Native, identity.SourceMachine, "s", 8), Accept, new string('b', 64)), default));
        Assert.Equal("transfer_capture_changed", refused.Message);
        Assert.Equal(0, target.Count("engine_session"));
    }

    [Fact]
    public async Task Refused_target_resume_ends_with_its_reason_closes_and_can_never_open_again()
    {
        using var target = new EngineFixture();
        await using var provider = new NativeProvider(target);
        var identity = Identity(target);
        var (start, _) = await provider.AdoptAsync(identity, Accept);
        var running = provider.Effects().ExecuteAsync(start, default);
        await provider.RefuseResumeAsync(-32000);
        var completed = provider.Complete(start, await running, 8);
        Assert.True(completed.Applied, completed.Error);
        Assert.Equal("ended", target.State("engine_session", "h"));
        var failed = provider.Ack(identity).Target!;
        Assert.Equal(HandoffTargetState.Failed, failed.State);
        Assert.Equal("denied", failed.ResumeReason);
        Assert.Null(failed.ClosedFingerprint);
        var commands = new EngineCommands(target.Machine, target.Limits);
        var wrong = target.Db.Writer.Write("test.wrong-reason", scope => commands.SealHandoffTarget(scope, new(identity, "other"), 1000));
        Assert.Null(wrong.Target!.ClosedFingerprint);
        var closed = target.Db.Writer.Write("test.close", scope => commands.SealHandoffTarget(scope, new(identity, "denied"), 1000));
        Assert.NotNull(closed.Target!.ClosedFingerprint);
        target.Reopen();
        Assert.NotNull(provider.Ack(identity).Target!.ClosedFingerprint);
        using var scope = target.Db.OpenSnapshot(default);
        Assert.Equal(HandoffFenceState.Closed, commands.ReadTargetHandoffFence(scope, "h").State);
    }
}
