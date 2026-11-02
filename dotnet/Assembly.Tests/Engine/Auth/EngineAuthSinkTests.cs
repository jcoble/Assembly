using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;
using Microsoft.Data.Sqlite;

namespace Assembly.Tests.Engine.Auth;

public sealed class EngineAuthSinkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Record_freezes_full_safe_facts_and_original_time_before_supplied_writer()
    {
        await using var f = new AuthRuntimeFixture(); var probe = f.Probe(); var raw = AuthRuntimeFixture.Facts("full\0" + new string('é', 40_000));
        using var json = JsonDocument.Parse(raw); f.Database.Arm();
        var record = Task.Run(() => f.Sink.RecordCheckAsync(probe, json.RootElement, "installed", "unverified", null, default));
        try { await f.Database.Entered.Task.WaitAsync(Timeout); json.Dispose(); f.Core.Time.Advance(TimeSpan.FromSeconds(1)); }
        finally { f.Database.Release(); }
        Assert.True((await record.WaitAsync(Timeout)).Applied);
        Assert.Equal(raw, f.Core.Text("SELECT facts_json FROM engine_provider WHERE id='codex/'"));
        Assert.Equal(1000, f.Core.Number("SELECT checked_ms FROM engine_provider WHERE id='codex/'"));
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("version")]
    public async Task Original_captured_probe_cannot_borrow_replacement_claim_or_current_provider_version(string replacement)
    {
        await using var f = new AuthRuntimeFixture(); var probe = f.Probe();
        if (replacement == "claim")
        {
            Assert.True(f.Apply(new CompleteEffect(new(EffectDisposition.Unknown, JsonSerializer.SerializeToElement((string?)null), "interrupted")), probe.EffectId, 1, new(ActorKind.System, "engine.worker")).Applied);
            f.Core.Time.Advance(TimeSpan.FromSeconds(1)); Assert.True(f.Apply(new ClaimEffect(), probe.EffectId, 2, new(ActorKind.System, "engine.worker")).Applied);
        }
        else Assert.True(f.Apply(new InterruptProviderCheck(), "codex/", 1, new(ActorKind.System, "engine.initialize")).Applied);
        using var json = JsonDocument.Parse(AuthRuntimeFixture.Facts());
        Assert.False((await f.Sink.RecordCheckAsync(probe, json.RootElement, "installed", "unverified", null, default)).Applied);
        Assert.Null(f.Core.Text("SELECT facts_json FROM engine_provider WHERE id='codex/'"));
    }

    [Fact]
    public async Task Safe_facts_storage_fault_rolls_back_callback_receipt_and_exact_input_can_retry()
    {
        await using var f = new AuthRuntimeFixture(); var probe = f.Probe(); using var json = JsonDocument.Parse(AuthRuntimeFixture.Facts());
        f.Core.Db.Writer.Write("test.sink.fault", s => s.Execute("CREATE TRIGGER sink_fault BEFORE UPDATE OF facts_json ON engine_provider BEGIN SELECT RAISE(ABORT,'facts fault'); END"));
        var count = f.Core.Count("engine_command_receipt");
        await Assert.ThrowsAsync<SqliteException>(() => f.Sink.RecordCheckAsync(probe, json.RootElement, "installed", "unverified", null, default));
        Assert.Equal(count, f.Core.Count("engine_command_receipt")); Assert.Null(f.Core.Text("SELECT facts_json FROM engine_provider WHERE id='codex/'"));
        f.Core.Db.Writer.Write("test.sink.unblock", s => s.Execute("DROP TRIGGER sink_fault"));
        Assert.True((await f.Sink.RecordCheckAsync(probe, json.RootElement, "installed", "unverified", null, default)).Applied);
    }

    [Fact]
    public async Task Progress_reads_only_bound_operation_version_and_preserves_each_waiting_generation()
    {
        await using var f = new AuthRuntimeFixture(); var begin = f.Begin();
        Assert.True(f.Sink.Progress(begin, "waiting_for_person", null).Applied);
        Assert.True(f.Sink.Progress(begin, "waiting_for_person", null).Applied);
        Assert.Equal(3, f.Core.Number("SELECT version FROM engine_sign_in WHERE id='operation'"));
        Assert.True(f.Sink.Progress(begin, "finishing", null).Applied); Assert.True(f.Sink.Progress(begin, "signed_in", null).Applied);
        Assert.Equal("signed_in", f.Core.Text("SELECT auth_state FROM engine_provider WHERE id='codex/'"));
        Assert.Null(f.Core.Text("SELECT facts_json FROM engine_provider WHERE id='codex/'"));
        Assert.False(f.Sink.Progress(begin, "waiting_for_person", null).Applied);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("claim")]
    public async Task Wrong_begin_binding_cannot_mutate_active_operation(string fault)
    {
        await using var f = new AuthRuntimeFixture(); var begin = f.Begin();
        begin = fault == "target" ? begin with { TargetId = "codex/other" } : begin with { BeginClaimAttempt = 2 };
        Assert.False(f.Sink.Progress(begin, "finishing", null).Applied); Assert.Equal("starting", f.Core.State("engine_sign_in", "operation"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_stop_between_callback_capture_and_writer_fences_both_fact_paths(bool progress)
    {
        await using var f = new AuthRuntimeFixture(); var probe = progress ? null : f.Probe(); var begin = progress ? f.Begin() : null;
        using var json = JsonDocument.Parse(AuthRuntimeFixture.Facts()); var before = f.Core.Count("engine_command_receipt"); f.Database.Arm();
        var pending = Task.Run(async () => progress ? f.Sink.Progress(begin!, "finishing", null) : await f.Sink.RecordCheckAsync(probe!, json.RootElement, "installed", "unverified", null, default));
        try
        {
            await f.Database.Entered.Task.WaitAsync(Timeout); await f.Runtime.DisposeAsync();
        }
        finally { f.Database.Release(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(Timeout));
        Assert.Equal(before, f.Core.Count("engine_command_receipt"));
    }

    [Fact]
    public async Task Unreviewed_method_unavailable_reason_cannot_mutate_state_or_events()
    {
        await using var f = new AuthRuntimeFixture(); var begin = f.Begin(); var count = f.Core.Count("engine_event");
        var result = f.Sink.Progress(begin, "failed", "method_unavailable");
        Assert.False(result.Applied); Assert.Equal("bad_args", result.Error);
        Assert.Equal("starting", f.Core.State("engine_sign_in", "operation")); Assert.Equal(count, f.Core.Count("engine_event"));
    }

    [Fact]
    public async Task Disposed_runtime_refuses_late_callbacks_without_a_new_receipt()
    {
        await using var f = new AuthRuntimeFixture(); var begin = f.Begin(); var before = f.Core.Count("engine_command_receipt");
        await f.Runtime.DisposeAsync(); Assert.ThrowsAny<OperationCanceledException>(() => f.Sink.Progress(begin, "finishing", null));
        Assert.Equal(before, f.Core.Count("engine_command_receipt"));
    }
}
