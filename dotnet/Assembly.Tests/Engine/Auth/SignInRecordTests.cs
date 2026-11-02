using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;

namespace Assembly.Tests.Engine.Auth;

public sealed class SignInRecordTests
{
    private static string AccountA => "acct-v1:" + new string('a', 64);
    private static string AccountB => "acct-v1:" + new string('b', 64);
    [Fact]
    public void Start_retains_exact_target_method_deadline_and_one_sessionless_begin()
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == AccountA);
        var result = f.Apply(new StartSignIn("codex", AccountA, "advertised-method", null), "operation");
        Assert.True(result.Applied, result.Error);
        Assert.Equal("starting", f.Text("engine_sign_in", "operation", "state"));
        Assert.Equal(AccountA, f.Text("engine_sign_in", "operation", "account_context"));
        Assert.Equal("advertised-method", f.Text("engine_sign_in", "operation", "method_id"));
        Assert.Equal(301000, f.Core.Number("SELECT deadline_ms FROM engine_sign_in WHERE id='operation'"));
        var effect = f.Effect("begin_sign_in/operation");
        Assert.NotNull(effect); Assert.Null(effect.SessionId); Assert.Null(effect.RunId);
        Assert.Equal("provider/codex/" + AccountA, effect.OrderKey);
        Assert.Equal(0, f.Core.Count("engine_session"));
    }

    [Fact]
    public void Second_start_same_target_refuses_but_other_account_is_independent()
    {
        using var f = new AuthRecordFixture((p, _) => p == "codex");
        var start = new StartSignIn("codex", AccountA, null, null);
        Assert.True(f.Apply(start, "first").Applied);
        var duplicate = f.Apply(start, "second");
        Assert.False(duplicate.Applied); Assert.Equal("already_running", duplicate.Error);
        Assert.True(f.Apply(start with { AccountContext = AccountB }, "third").Applied);
        Assert.Equal(2, f.Core.Count("engine_sign_in")); Assert.Equal(2, f.Core.Count("engine_outbox"));
    }

    [Fact]
    public void Same_start_request_replays_exact_result_before_live_target_removal()
    {
        var configured = true; using var f = new AuthRecordFixture((p, a) => configured && p == "codex" && a == "");
        var context = f.Context("operation"); var start = new StartSignIn("codex", "", null, null);
        var result = f.Apply(context, start); Assert.True(result.Applied, result.Error);
        configured = false; Assert.Equal(result, f.Apply(context with { NowMs = 9999 }, start));
        Assert.Equal(1, f.Core.Count("engine_sign_in")); Assert.Equal(1, f.Core.Count("engine_outbox"));
    }

    [Fact]
    public void Ready_provider_refuses_exact_running_session_count_but_idle_or_signed_out_does_not()
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == "");
        f.SeedProvider(state: "signed_in");
        f.Core.SeedSession("running-1", "running"); f.Core.SeedSession("running-2", "running");
        f.Core.SeedSession("other", "running", provider: "claude"); f.Core.SeedSession("idle");
        var refused = f.Apply(new StartSignIn("codex", "", null, null), "ready-op");
        Assert.False(refused.Applied); Assert.Equal("conflict_running:2", refused.Error);
        Assert.Equal(0, f.Core.Count("engine_sign_in")); Assert.Equal(0, f.Core.Count("engine_outbox"));
        f.Core.Db.Writer.Write("test.auth.negative", s => s.Execute("UPDATE engine_provider SET auth_state='needs_sign_in'"));
        Assert.True(f.Apply(new StartSignIn("codex", "", null, null), "recovery-op").Applied);
    }

    [Fact]
    public void Start_refuses_corrupt_retained_provider_row_without_borrowing_or_overwriting_its_id()
    {
        using var f = new AuthRecordFixture((p, a) => p == "provider/team" && a == AccountA);
        // Deliberate malformed retained row, not two valid opaque identities.
        f.SeedProvider("provider", "team/" + AccountA);
        var result = f.Apply(new StartSignIn("provider/team", AccountA, null, null), "operation");
        Assert.False(result.Applied); Assert.Equal("unavailable", result.Error);
        Assert.Equal("provider", f.Text("engine_provider", "provider/team/" + AccountA, "provider"));
        Assert.Equal(0, f.Core.Count("engine_sign_in")); Assert.Equal(0, f.Core.Count("engine_outbox"));
    }

    [Fact]
    public void Arbitrary_origin_need_cannot_create_operation()
    {
        using var f = new AuthRecordFixture((_, _) => true);
        var result = f.Apply(new StartSignIn("codex", "", null, "foreign-need"), "operation");
        Assert.False(result.Applied); Assert.Equal(0, f.Core.Count("engine_sign_in"));
    }

    [Fact]
    public void Stale_cancel_preserves_active_operation_then_current_cancel_stops_unclaimed_begin_atomically()
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == "");
        Assert.True(f.Apply(new StartSignIn("codex", "", null, null), "operation").Applied);
        var stale = f.Apply(new CancelSignIn(), "operation", 0);
        Assert.False(stale.Applied); Assert.Equal("version_conflict", stale.Error);
        Assert.Equal("starting", f.Text("engine_sign_in", "operation", "state"));
        Assert.True(f.Apply(new CancelSignIn(), "operation", 1).Applied);
        Assert.Equal("cancelled", f.Text("engine_sign_in", "operation", "state"));
        Assert.Equal("failed", f.Text("engine_outbox", "begin_sign_in/operation", "state"));
        Assert.Equal("provider/codex/", f.Effect("cancel_sign_in/operation").OrderKey);
    }

    [Fact]
    public void Only_fixed_worker_can_expire_at_the_actual_deadline()
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == "");
        Assert.True(f.Apply(new StartSignIn("codex", "", null, null), "operation").Applied);
        var early = f.Apply(new ExpireSignIn(), "operation", 1, new(ActorKind.System, "engine.worker"));
        Assert.False(early.Applied); Assert.Equal("not_expired", early.Error);
        f.Core.Time.Advance(TimeSpan.FromSeconds(300));
        Assert.False(f.Apply(new ExpireSignIn(), "operation", 1, new(ActorKind.System, "socket")).Applied);
        Assert.True(f.Apply(new ExpireSignIn(), "operation", 1, new(ActorKind.System, "engine.worker")).Applied);
        Assert.Equal("expired", f.Text("engine_sign_in", "operation", "state"));
        Assert.NotNull(f.Effect("cancel_sign_in/operation"));
    }

    [Fact]
    public void Started_receipt_never_establishes_login_and_late_progress_cannot_revive_cancelled_attempt()
    {
        using var f = new AuthRecordFixture((p, a) => p == "codex" && a == ""); f.SeedProvider();
        Assert.True(f.Apply(new StartSignIn("codex", "", null, null), "operation").Applied);
        Assert.True(f.Apply(new ClaimEffect(), "begin_sign_in/operation", 0, new(ActorKind.System, "engine.worker")).Applied);
        var binding = new SignInAttemptBinding("operation", "codex/", "begin_sign_in/operation", 1);
        Assert.False(f.Apply(new SignInProgress("signed_in", null, binding), "operation", 1, new(ActorKind.System, "sign_in.effects")).Applied);
        Assert.True(f.Apply(new SignInProgress("waiting_for_person", null, binding), "operation", 1, new(ActorKind.System, "sign_in.effects")).Applied);
        Assert.Equal("needs_sign_in", f.Text("engine_provider", "codex/", "auth_state"));
        Assert.True(f.Apply(new CancelSignIn(), "operation", 2).Applied);
        Assert.False(f.Apply(new SignInProgress("finishing", null, binding), "operation", 3, new(ActorKind.System, "sign_in.effects")).Applied);
        Assert.Equal("cancelled", f.Text("engine_sign_in", "operation", "state"));
    }
}
