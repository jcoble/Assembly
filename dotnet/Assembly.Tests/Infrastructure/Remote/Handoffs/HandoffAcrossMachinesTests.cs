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
    public void A_missing_admission_is_not_evidence_that_an_unknown_adopt_cannot_arrive()
    {
        var target = Target(Evidence(HandoffTargetState.NotAdopted) with { StartEffectId = null, StartAttempt = null, SessionVersion = 0 })
            with
        { Receipt = null, Fingerprint = null, ReceiptKey = null };
        Assert.Null(HandoffCoordinator.TerminalPhase(Source(), target));
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
