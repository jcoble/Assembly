using Assembly.Infrastructure.Engine.Handoff;
using System.Text.Json;
using Assembly.Engine.Contracts;
using Assembly.Engine.Mirror;
using Assembly.Engine.Sessions;
using Assembly.Tests.Engine;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Hosting;
using Assembly.Infrastructure.Engine;

namespace Assembly.Tests.Infrastructure.Remote;

/// <summary>Source-derived coordinator policy unit fixtures; these are not native capture/quiescence proof.</summary>
public sealed class HandoffAcrossMachinesTests
{
    private static readonly HandoffIdentity Identity = new("h", "source-engine", "source-session", "target-engine", 7, 8);

    [Theory]
    [InlineData(HandoffGate.Waiting)]
    [InlineData(HandoffGate.Unknown)]
    [InlineData(HandoffGate.Refused)]
    public void An_uncertain_target_never_restores_an_accepted_source(HandoffGate gate)
    {
        var target = Target(null) with { Gate = gate, Reason = "unavailable" };
        Assert.Null(HandoffCoordinator.TerminalPhase(Source(), target));
    }

    [Fact]
    public void A_target_display_state_without_owning_operation_evidence_is_not_a_terminal_proof()
    {
        var target = Target(null) with { HandoffState = "idle", HandoffVersion = 5 };
        Assert.Null(HandoffCoordinator.TerminalPhase(Source(), target));
    }

    [Fact]
    public void Actual_matching_resumed_evidence_requests_the_fixed_source_terminal_phase()
    {
        var evidence = Evidence(HandoffTargetState.Resumed);
        var phase = HandoffCoordinator.TerminalPhase(Source(), Target(evidence));
        Assert.NotNull(phase);
        Assert.Equal(Identity, phase.Identity);
        Assert.Equal(HandoffPhase.Resumed, phase.Phase);
        Assert.Equal(2, phase.ObservedHandoffVersion);
        Assert.Null(phase.Reason);
        Assert.Same(evidence, phase.Target);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("writer_elsewhere")]
    [InlineData("no_folder")]
    [InlineData("transient")]
    public void Actual_closed_target_failure_preserves_its_resume_reason(string reason)
    {
        var evidence = Evidence(HandoffTargetState.Failed) with { ResumeReason = reason, ClosedFingerprint = "unit-closed" };
        var phase = HandoffCoordinator.TerminalPhase(Source(), Target(evidence));
        Assert.NotNull(phase);
        Assert.Equal(HandoffPhase.Failed, phase.Phase);
        Assert.Equal(reason, phase.Reason);
        Assert.Equal(2, phase.ObservedHandoffVersion);
        Assert.Same(evidence, phase.Target);
    }

    [Fact]
    public void Failed_display_or_dead_effect_without_the_closed_fence_cannot_restore_source()
    {
        var evidence = Evidence(HandoffTargetState.Failed) with { ResumeReason = "missing", ClosedFingerprint = null };
        Assert.Null(HandoffCoordinator.TerminalPhase(Source(), Target(evidence)));
    }

    [Fact]
    public void A_sealed_not_adopted_target_is_the_known_failure_path()
    {
        var evidence = Evidence(HandoffTargetState.NotAdopted) with { StartEffectId = null, StartAttempt = null, SessionVersion = 0 };
        var target = Target(evidence) with { Reason = "no_folder", Receipt = new(false, false, "handoff_not_adopted", 0, 0, 8) };
        var phase = HandoffCoordinator.TerminalPhase(Source(), target);
        Assert.NotNull(phase);
        Assert.Equal(HandoffPhase.Failed, phase.Phase);
        Assert.Equal("no_folder", phase.Reason);
    }

    [Fact]
    public void A_missing_admission_is_not_evidence_that_an_unknown_adopt_cannot_arrive()
    {
        var target = Target(Evidence(HandoffTargetState.NotAdopted) with { StartEffectId = null, StartAttempt = null, SessionVersion = 0 })
            with
        { Receipt = null, Fingerprint = null, ReceiptKey = null };
        Assert.Null(HandoffCoordinator.TerminalPhase(Source(), target));
    }

    [Fact]
    public void Stale_identity_epoch_or_attempt_evidence_cannot_finalize_the_source()
    {
        var evidence = Evidence(HandoffTargetState.Resumed);
        foreach (var invalid in new[]
        {
            evidence with { Identity = Identity with { SourceSessionId = "replacement" } },
            evidence with { Epoch = 7 },
            evidence with { StartEffectId = null },
            evidence with { StartEffectId = "start/another-handoff/1" },
            evidence with { StartAttempt = null },
            evidence with { AdmissionFingerprint = "" }
        }) Assert.Null(HandoffCoordinator.TerminalPhase(Source(), Target(invalid)));
    }

    [Theory]
    [InlineData("requested", 1)]
    [InlineData("resumed", 3)]
    [InlineData("failed", 3)]
    [InlineData("accepted", 3)]
    public void An_advanced_or_different_source_phase_is_never_sent_backward(string state, long version)
    {
        var source = Source() with { HandoffState = state, HandoffVersion = version };
        Assert.Null(HandoffCoordinator.TerminalPhase(source, Target(Evidence(HandoffTargetState.Resumed))));
    }

    [Fact]
    public void Candidate_discovery_requires_the_exact_live_generation_of_both_owners()
    {
        using var fixture = new EngineFixture();
        fixture.SeedSession("s", holder: fixture.Machine, epoch: 7);
        Assert.True(fixture.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        var generation = fixture.Db.Writer.Write("unit.mirror", scope =>
        {
            MirrorApply.Open(scope, "target", "test-target", 1000);
            return MirrorApply.EndSnapshot(scope, "target", 0, null, 1000).Generation;
        });
        using var read = fixture.Db.OpenSnapshot(default);
        Assert.Empty(HandoffCoordinator.Candidates(read, fixture.Machine, []));
        Assert.Empty(HandoffCoordinator.Candidates(read, fixture.Machine, [new("target", generation + 1)]));
        var candidate = Assert.Single(HandoffCoordinator.Candidates(read, fixture.Machine, [new("target", generation)]));
        Assert.Equal(new HandoffIdentity("h", fixture.Machine, "s", "target", 7, 8), candidate.Identity);
    }

    [Fact]
    public void A_mirrored_source_handoff_to_local_is_discovery_only_and_keeps_the_actual_source_identity()
    {
        using var fixture = new EngineFixture();
        var generation = fixture.Db.Writer.Write("unit.mirror", scope =>
        {
            MirrorApply.Open(scope, "remote-source", "test-source", 1000);
            var row = JsonSerializer.SerializeToElement(new
            {
                entityKind = "handoff",
                machine = "remote-source",
                id = "h",
                version = 2,
                sessionId = "remote-session",
                oldHolder = "remote-source",
                newHolder = fixture.Machine,
                oldEpoch = 7,
                nextEpoch = 8,
                state = "accepted",
                createdMs = 1000,
                updatedMs = 1000
            });
            MirrorApply.SnapshotPage(scope, "remote-source", [row]);
            return MirrorApply.EndSnapshot(scope, "remote-source", 0, null, 1000).Generation;
        });
        using var read = fixture.Db.OpenSnapshot(default);
        var candidate = Assert.Single(HandoffCoordinator.Candidates(read, fixture.Machine, [new("remote-source", generation)]));
        Assert.Equal("remote-source", candidate.Identity.SourceMachine);
        Assert.Equal("remote-session", candidate.Identity.SourceSessionId);
        Assert.Equal(fixture.Machine, candidate.Identity.TargetMachine);
        Assert.Equal(2, candidate.Version);
        Assert.Equal("accepted", candidate.State);
        Assert.Equal(0, fixture.Count("engine_handoff")); // Discovery never creates local owning transitions.
    }

    [Fact]
    public async Task Local_owning_ack_and_phase_use_the_same_runtime_writer_and_leave_pending_release_unadmitted()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var fixture = new EngineFixture(topics: topics);
        await using var runtime = EngineRuntime.Start(fixture.Db, topics, fixture.Time, fixture.Limits,
            effectsReady: new TaskCompletionSource().Task);
        fixture.SeedSession("s", holder: fixture.Machine, epoch: 7);
        Assert.True(fixture.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        await using var coordinator = new HandoffCoordinator(runtime, new MachineForward(_ => null), []);
        var identity = new HandoffIdentity("h", fixture.Machine, "s", "target", 7, 8);
        var observed = coordinator.ReadLocalAck(identity, default);
        Assert.Equal("requested", observed.HandoffState);
        Assert.Equal(1, observed.HandoffVersion);
        var phase = coordinator.ApplyLocalPhase(new(identity, HandoffPhase.Accept, 1, null, null), default);
        Assert.Equal(HandoffGate.Waiting, phase.Gate);
        Assert.Equal("unavailable", phase.Reason);
        Assert.Null(phase.Receipt);
        Assert.Equal("requested", fixture.State("engine_handoff", "h"));
        Assert.Equal(0, fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'handoff/v1/%'"));
    }

    [Fact]
    public async Task Cancelled_local_phase_cannot_allocate_a_receipt_or_change_the_source()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var fixture = new EngineFixture(topics: topics);
        await using var runtime = EngineRuntime.Start(fixture.Db, topics, fixture.Time, fixture.Limits,
            effectsReady: new TaskCompletionSource().Task);
        fixture.SeedSession("s", holder: fixture.Machine, epoch: 7);
        Assert.True(fixture.Apply(new RequestHandoff("s", "target"), "h", epoch: 7).Applied);
        await using var coordinator = new HandoffCoordinator(runtime, new MachineForward(_ => null), []);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var identity = new HandoffIdentity("h", fixture.Machine, "s", "target", 7, 8);
        Assert.ThrowsAny<OperationCanceledException>(() => coordinator.ApplyLocalPhase(
            new(identity, HandoffPhase.Accept, 1, null, null), cancelled.Token));
        Assert.Equal("requested", fixture.State("engine_handoff", "h"));
        Assert.Equal(0, fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'handoff/v1/%'"));
    }

    [Fact]
    public async Task Start_is_idempotent_and_disposal_joins_the_owned_pushed_lifetime()
    {
        var topics = new global::Assembly.Infrastructure.Topics.Topics();
        using var fixture = new EngineFixture(topics: topics);
        await using var runtime = EngineRuntime.Start(fixture.Db, topics, fixture.Time, fixture.Limits,
            effectsReady: new TaskCompletionSource().Task);
        var coordinator = new HandoffCoordinator(runtime, new MachineForward(_ => throw new InvalidOperationException("No by-name transport")), []);
        coordinator.Start(); coordinator.Start();
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await coordinator.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(coordinator.Start);
    }

    [Fact]
    public async Task Remote_source_reconciliation_reads_the_actual_owner_and_does_not_accept_before_native_check()
    {
        await using var hosts = new TwoHosts(forwardCommands: true,
            machineLauncher: new Assembly.Tests.Features.Chat.Sessions.FakeLauncher(), checkouts: Path.GetTempPath());
        await hosts.Start();
        hosts.Remote.Fixture.SeedSession("handoff-source", holder: hosts.Remote.Runtime.Machine, epoch: 7);
        await hosts.StartMirror();
        var identity = new HandoffIdentity("handoff", hosts.Remote.Runtime.Machine, "handoff-source", hosts.Mac.Runtime.Machine, 7, 8);
        Assert.True(hosts.Remote.Fixture.Apply(new RequestHandoff(identity.SourceSessionId, identity.TargetMachine), identity.HandoffId, epoch: 7).Applied);
        var mirrors = hosts.Connections.CaptureMirrorOwners().Select(owner => owner.Mirror).ToArray();
        await using var coordinator = new HandoffCoordinator(hosts.Mac.Runtime, hosts.Mac.Forwarder!, mirrors);
        var actual = await coordinator.ReconcileAsync(identity, default);
        Assert.NotNull(actual);
        Assert.Equal(identity, actual.Identity);
        Assert.Equal("requested", actual.HandoffState);
        Assert.Equal(1, actual.HandoffVersion);
        Assert.Null(actual.Receipt);
        Assert.Equal("handing_off", hosts.Remote.Fixture.State("engine_session", identity.SourceSessionId));
        Assert.Equal(0, hosts.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'handoff/v1/%'"));
        Assert.Equal(0, hosts.Mac.Fixture.Number("SELECT COUNT(*) FROM engine_session WHERE id='handoff'"));
    }

    [Fact]
    public async Task A_disconnected_source_waits_without_using_its_retained_mirror_as_owning_ack()
    {
        await using var hosts = new TwoHosts(forwardCommands: true,
            machineLauncher: new Assembly.Tests.Features.Chat.Sessions.FakeLauncher(), checkouts: Path.GetTempPath());
        await hosts.Start();
        hosts.Remote.Fixture.SeedSession("handoff-source", holder: hosts.Remote.Runtime.Machine, epoch: 7);
        var identity = new HandoffIdentity("handoff", hosts.Remote.Runtime.Machine, "handoff-source", hosts.Mac.Runtime.Machine, 7, 8);
        Assert.True(hosts.Remote.Fixture.Apply(new RequestHandoff(identity.SourceSessionId, identity.TargetMachine), identity.HandoffId, epoch: 7).Applied);
        await hosts.StartMirror();
        var mirrors = hosts.Connections.CaptureMirrorOwners().Select(owner => owner.Mirror).ToArray();
        await using var coordinator = new HandoffCoordinator(hosts.Mac.Runtime, hosts.Mac.Forwarder!, mirrors);
        await hosts.DisconnectMirror();
        Assert.Null(await coordinator.ReconcileAsync(identity, default));
        Assert.Equal("requested", hosts.Remote.Fixture.State("engine_handoff", identity.HandoffId));
        Assert.Equal(0, hosts.Remote.Fixture.Number("SELECT COUNT(*) FROM engine_command_receipt WHERE command_id LIKE 'handoff/v1/%'"));
    }

    private static HandoffAck Source() => new(Identity, HandoffGate.Recorded, null, "unit-accept-key", "unit-source-accept",
        new(true, false, null, 2, 4, 8), 2, "accepted", null);
    private static HandoffAck Target(HandoffTargetEvidence? evidence) => new(Identity, HandoffGate.Recorded, null,
        "unit-admission-key", "unit-admission", new(true, false, null, 1, 2, 8), null, null, evidence);
    private static HandoffTargetEvidence Evidence(HandoffTargetState state) => new(Identity, state, "unit-admission", null,
        3, 8, null, "start/h/1", 1);
}
