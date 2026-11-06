using System.Text.Json;
using System.Net.WebSockets;
using System.Threading.Channels;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine.Handoff;

/// <summary>The desktop's existing cross-machine handoff driver; owning acknowledgments govern each phase.</summary>
internal sealed class HandoffCoordinator : IAsyncDisposable
{
    private readonly EngineRuntime _runtime;
    private readonly IEngineRuntime _engine;
    private readonly MachineForward _forward;
    private readonly IReadOnlyCollection<EngineMirror> _mirrors;
    private readonly CancellationTokenSource _stop;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock _lifetime = new();
    private readonly Dictionary<string, RemoteOwner> _owners = new(StringComparer.Ordinal);
    private readonly HashSet<HandoffIdentity> _uncertainSeal = [];
    private readonly HashSet<HandoffIdentity> _uncertainTerminal = [];
    private IDisposable? _localEvents;
    private Task _run = Task.CompletedTask;
    private Task? _dispose;
    private bool _started;

    private sealed record RemoteOwner(EngineMirror Mirror, EngineMirror.LiveMirrorBinding Live, IDisposable Events);

    public HandoffCoordinator(EngineRuntime mac, MachineForward forward, IReadOnlyCollection<EngineMirror> mirrors)
    {
        ArgumentNullException.ThrowIfNull(forward);
        (_runtime, _engine, _forward, _mirrors) = (mac, mac, forward, mirrors);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(mac.Stopping);
    }

    public void Start()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (_started) return;
            _started = true;
            _localEvents = _runtime.Publisher.Register(OnEvent, out _, Wake);
            foreach (var mirror in _mirrors) mirror.CaughtUp += Wake;
            _run = RunAsync(ready.Task);
        }
        ready.SetResult();
        Wake();
    }

    private void Wake() => _wake.Writer.TryWrite(true);
    private void OnEvent(EngineEvent item)
    {
        if (item.EntityKind is "handoff" or "session" or "effect" or "needs_me") Wake();
    }

    private async Task RunAsync(Task ready)
    {
        await ready;
        try
        {
            await foreach (var _ in _wake.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    var owners = await CaptureOwnersAsync(_stop.Token);
                    Candidate? after = null;
                    while (true)
                    {
                        var page = _engine.Read(scope => Candidates(scope, _runtime.Machine, owners, after), _stop.Token);
                        foreach (var candidate in page)
                        {
                            if (!await OwnersCurrentAsync(candidate.Identity, _stop.Token)) continue;
                            await ReconcileCapturedAsync(candidate.Identity, _stop.Token);
                        }
                        if (page.Count < 200) break;
                        after = page[^1];
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { /* The captured owner generation ended; wait for CaughtUp. */ }
                catch (Exception error) when (error is MachineUnreachable or CallFailed or IOException or JsonException or KeyNotFoundException or ObjectDisposedException)
                {
                    // Reconnection/CaughtUp or the next owning change wakes recovery; never poll or resend.
                    Console.Error.WriteLine("Handoff coordinator waits for owning state: " + error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task<IReadOnlyList<MachineStartMirrorGeneration>> CaptureOwnersAsync(CancellationToken ct)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mirror in _mirrors)
        {
            var live = await mirror.CaptureLiveBindingAsync(ct);
            if (live is null) continue;
            if (live.Machine == _runtime.Machine || !current.Add(live.Machine)) throw new CallFailed("ambiguous engine owner");
            if (_owners.TryGetValue(live.Machine, out var old)
                && ReferenceEquals(old.Mirror, mirror) && ReferenceEquals(old.Live, live)) continue;
            if (old is not null) old.Events.Dispose();
            // Register before discovery so an owning change racing the bounded read queues the next pass.
            _owners[live.Machine] = new(mirror, live, _runtime.PublisherFor(live.Machine).Register(OnEvent, out _, Wake));
        }
        foreach (var machine in _owners.Keys.Where(machine => !current.Contains(machine)).ToArray())
        {
            _owners[machine].Events.Dispose();
            _owners.Remove(machine);
        }
        return _owners.Select(pair => new MachineStartMirrorGeneration(pair.Key, pair.Value.Live.Generation)).ToArray();
    }

    private async Task<bool> OwnersCurrentAsync(HandoffIdentity identity, CancellationToken ct)
    {
        foreach (var machine in new[] { identity.SourceMachine, identity.TargetMachine })
            if (machine != _runtime.Machine && (!_owners.TryGetValue(machine, out var owner)
                || !await owner.Mirror.IsCurrentLiveBindingAsync(owner.Live, ct))) return false;
        return true;
    }

    internal async Task<HandoffAck?> ReconcileAsync(HandoffIdentity identity, CancellationToken ct)
    {
        await CaptureOwnersAsync(ct);
        return await OwnersCurrentAsync(identity, ct) ? await ReconcileCapturedAsync(identity, ct) : null;
    }

    private async Task<HandoffAck?> ReconcileCapturedAsync(HandoffIdentity identity, CancellationToken ct)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, OwnerStopping(identity.SourceMachine), OwnerStopping(identity.TargetMachine));
        ct = operation.Token;
        var source = await ReadOwnerAckAsync(identity.SourceMachine, identity, ct);
        if (source.HandoffState is "resumed" or "failed")
        {
            _uncertainSeal.Remove(identity); _uncertainTerminal.Remove(identity);
            return source;
        }
        // T9's actual native eligibility/workspace check must precede Accept. Its exact check/prepare/stat/write
        // APIs and target launch-context producer are not supplied yet. Leave requested work with its real ack.
        if (!AcceptedSource(source)) return source;

        var target = await ReadOwnerAckAsync(identity.TargetMachine, identity, ct);
        if (target.Gate == HandoffGate.Recorded && target.Target is not null) _uncertainSeal.Remove(identity);
        if (target.Gate == HandoffGate.Waiting && target.Receipt is null && !_uncertainSeal.Contains(identity))
        {
            // Recovered accepted work with no admission may still have an Adopt in flight. The fixed target seal
            // serializes against it; an absent row/reply never authorizes source restoration by itself.
            source = await ReadOwnerAckAsync(identity.SourceMachine, identity, ct);
            if (!AcceptedSource(source)) return source;
            target = await ReadOwnerAckAsync(identity.TargetMachine, identity, ct);
            if (target.Gate == HandoffGate.Waiting && target.Receipt is null)
                target = await SealTargetAsync(identity, "handoff_not_adopted", ct) ?? target;
        }
        else if (target is { Gate: HandoffGate.Recorded, Target: { State: HandoffTargetState.Failed, ClosedFingerprint: null, ResumeReason: { } reason } }
                 && !_uncertainSeal.Contains(identity))
        {
            source = await ReadOwnerAckAsync(identity.SourceMachine, identity, ct);
            if (!AcceptedSource(source)) return source;
            target = await SealTargetAsync(identity, reason, ct) ?? target;
        }

        var phase = TerminalPhase(source, target);
        if (phase is null || _uncertainTerminal.Contains(identity)) return source;
        // Re-read both actual owners before this step. Keep the original phase/evidence unchanged: a newer read
        // can stop it, but cannot refresh a previously prepared command's version, native attempt or reason.
        source = await ReadOwnerAckAsync(identity.SourceMachine, identity, ct);
        target = await ReadOwnerAckAsync(identity.TargetMachine, identity, ct);
        if (TerminalPhase(source, target) != phase) return source;
        if (!await OwnersCurrentAsync(identity, ct)) return source;
        try
        {
            return identity.SourceMachine == _runtime.Machine ? ApplyLocalPhase(phase, ct)
                : await _forward.HandoffPhaseAsync(_owners[identity.SourceMachine].Live, phase, ct);
        }
        catch (Exception error) when (LostReply(error))
        {
            _uncertainTerminal.Add(identity);
            var reconciled = await ReadAfterLostReplyAsync(identity.SourceMachine, identity, ct);
            if (reconciled?.HandoffState is "resumed" or "failed")
            {
                _uncertainTerminal.Remove(identity); _uncertainSeal.Remove(identity);
            }
            return reconciled;
        }
        catch (OperationCanceledException)
        {
            _uncertainTerminal.Add(identity);
            throw;
        }
    }

    private static bool AcceptedSource(HandoffAck source) => source.HandoffState == "accepted" && source.HandoffVersion == 2
        && source.Gate is HandoffGate.Recorded or HandoffGate.Advanced && source.Receipt is { Applied: true }
        && !string.IsNullOrEmpty(source.ReceiptKey) && !string.IsNullOrEmpty(source.Fingerprint);

    private CancellationToken OwnerStopping(string machine) => machine == _runtime.Machine ? _runtime.Stopping
        : _owners.TryGetValue(machine, out var owner) ? owner.Live.Invalidated : new CancellationToken(true);

    private async Task<HandoffAck> ReadOwnerAckAsync(string machine, HandoffIdentity identity, CancellationToken ct)
    {
        if (!await OwnersCurrentAsync(identity, ct)) throw new CallFailed("mirror_changed");
        return machine == _runtime.Machine ? ReadLocalAck(identity, ct)
            : await _forward.HandoffAckAsync(_owners[machine].Live, identity, ct);
    }

    private async Task<HandoffAck?> SealTargetAsync(HandoffIdentity identity, string reason, CancellationToken ct)
    {
        if (!await OwnersCurrentAsync(identity, ct)) return null;
        // The target writer rechecks actual admission/terminal/quiescence in the SAME scope as its fixed seal.
        var before = await ReadOwnerAckAsync(identity.TargetMachine, identity, ct);
        if (before.Gate is HandoffGate.Unknown or HandoffGate.Refused) return before;
        try
        {
            var args = new HandoffSealArgs(identity, reason);
            return identity.TargetMachine == _runtime.Machine ? _engine.Write(scope =>
            {
                ct.ThrowIfCancellationRequested();
                _runtime.Commands.ReadHandoffAck(scope, identity);
                return _runtime.Commands.SealHandoffTarget(scope, args, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
            }) : await _forward.HandoffSealAsync(_owners[identity.TargetMachine].Live, args, ct);
        }
        catch (Exception error) when (LostReply(error))
        {
            _uncertainSeal.Add(identity);
            return await ReadAfterLostReplyAsync(identity.TargetMachine, identity, ct);
        }
        catch (OperationCanceledException)
        {
            _uncertainSeal.Add(identity);
            throw;
        }
    }

    private async Task<HandoffAck?> ReadAfterLostReplyAsync(string machine, HandoffIdentity identity, CancellationToken ct)
    {
        try { return await ReadOwnerAckAsync(machine, identity, ct); }
        catch (Exception error) when (LostReply(error)) { return null; }
    }

    private static bool LostReply(Exception error) => error is MachineUnreachable or IOException or WebSocketException or ObjectDisposedException or JsonException
        || error is CallFailed { Message: "mirror_changed" };

    internal HandoffAck ReadLocalAck(HandoffIdentity identity, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _engine.Read(scope => _runtime.Commands.ReadHandoffAck(scope, identity), ct);
    }

    internal HandoffAck ApplyLocalPhase(HandoffPhaseArgs args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _engine.Write(scope =>
        {
            ct.ThrowIfCancellationRequested();
            var current = _runtime.Commands.ReadHandoffAck(scope, args.Identity);
            if (current.Gate is HandoffGate.Unknown or HandoffGate.Refused
                || current.HandoffVersion != args.ObservedHandoffVersion
                || current.HandoffState is "resumed" or "failed") return current;
            // The canonical core mints the original fixed context and shares readiness with its transition,
            // including pre-journal Waiting. The coordinator never refreshes or constructs that context.
            return _runtime.Commands.ApplyHandoffPhase(scope, args, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        });
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? ready = null;
        Task completion;
        lock (_lifetime)
        {
            if (_dispose is null)
            {
                ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _dispose = DisposeAsync(ready.Task);
            }
            completion = _dispose;
        }
        ready?.SetResult();
        return new(completion);
    }

    private async Task DisposeAsync(Task ready)
    {
        await ready;
        _localEvents?.Dispose();
        foreach (var mirror in _mirrors) mirror.CaughtUp -= Wake;
        try { await _stop.CancelAsync(); }
        finally
        {
            try { await _run; }
            finally
            {
                foreach (var owner in _owners.Values) owner.Events.Dispose();
                _owners.Clear(); _uncertainSeal.Clear(); _uncertainTerminal.Clear();
                _wake.Writer.TryComplete();
                _stop.Dispose();
            }
        }
    }

    internal sealed record Candidate(HandoffIdentity Identity, long Version, string State);

    // Mirrors supply bounded discovery only. Every transition still starts with a typed actual-owner acknowledgment.
    internal static IReadOnlyList<Candidate> Candidates(IReadScope scope, string local,
        IReadOnlyList<MachineStartMirrorGeneration> owners, Candidate? after = null)
    {
        var eligible = JsonSerializer.Serialize(owners, EngineJson.Default.IReadOnlyListMachineStartMirrorGeneration);
        return scope.Query("""
            WITH live AS (
                SELECT m.machine,m.generation FROM json_each($owners) supplied JOIN engine_mirror m
                  ON m.machine=json_extract(supplied.value,'$.machine')
                  AND m.generation=json_extract(supplied.value,'$.generation') AND m.state='live'
            ), pending AS (
                SELECT machine AS source,id AS handoff,session_id AS session,new_holder AS target,
                    old_epoch AS oldEpoch,next_epoch AS nextEpoch,version,state
                FROM engine_handoff WHERE machine=$local AND state IN ('requested','accepted')
                UNION ALL
                SELECT r.machine,r.entity_id,json_extract(r.row_json,'$.sessionId'),json_extract(r.row_json,'$.newHolder'),
                    json_extract(r.row_json,'$.oldEpoch'),json_extract(r.row_json,'$.nextEpoch'),r.version,json_extract(r.row_json,'$.state')
                FROM engine_mirror_row r JOIN live owner ON owner.machine=r.machine AND owner.generation=r.generation
                WHERE r.entity_kind='handoff' AND json_extract(r.row_json,'$.machine')=r.machine
                    AND json_extract(r.row_json,'$.state') IN ('requested','accepted')
            ) SELECT source,handoff,session,target,oldEpoch,nextEpoch,version,state FROM pending p
            WHERE source<>target AND (target=$local OR EXISTS(SELECT 1 FROM live WHERE machine=p.target))
              AND ($afterSource IS NULL OR source>$afterSource OR source=$afterSource AND handoff>$afterHandoff)
            ORDER BY source,handoff LIMIT 200
            """, row => new Candidate(new(row.Text(1), row.Text(0), row.Text(2), row.Text(3), row.Long(4), row.Long(5)),
                row.Long(6), row.Text(7)), new("$owners", eligible), new("$local", local),
            new("$afterSource", after?.Identity.SourceMachine), new("$afterHandoff", after?.Identity.HandoffId));
    }

    /// <summary>Pure selection only. The source owning writer revalidates all target evidence before admission.</summary>
    internal static HandoffPhaseArgs? TerminalPhase(HandoffAck source, HandoffAck target)
    {
        if (source.Identity != target.Identity || source.HandoffState != "accepted" || source.HandoffVersion != 2
            || source.Gate is not (HandoffGate.Recorded or HandoffGate.Advanced)
            || source.Receipt is not { Applied: true } || string.IsNullOrEmpty(source.Fingerprint)
            || target.Gate is not (HandoffGate.Recorded or HandoffGate.Advanced)
            || target.Target is not { } evidence || evidence.Identity != source.Identity
            || evidence.Epoch != source.Identity.NextEpoch || string.IsNullOrEmpty(evidence.AdmissionFingerprint)
            || target.Receipt is null || string.IsNullOrEmpty(target.ReceiptKey) || string.IsNullOrEmpty(target.Fingerprint))
            return null;

        if (evidence.State == HandoffTargetState.NotAdopted)
            return target.Receipt is { Applied: false, Error: "handoff_not_adopted" }
                && target.Fingerprint == evidence.AdmissionFingerprint && evidence.SessionVersion == 0
                && evidence.StartEffectId is null && evidence.StartAttempt is null && !string.IsNullOrEmpty(target.Reason)
                ? new(source.Identity, HandoffPhase.Failed, 2, target.Reason, evidence) : null;

        if (target.Receipt is not { Applied: true } || evidence.SessionVersion < 1
            || evidence.StartAttempt is not > 0
            || evidence.StartEffectId != "start/" + source.Identity.HandoffId + "/" + evidence.StartAttempt)
            return null;
        return evidence.State switch
        {
            HandoffTargetState.Resumed => new(source.Identity, HandoffPhase.Resumed, 2, null, evidence),
            HandoffTargetState.Failed when !string.IsNullOrEmpty(evidence.ClosedFingerprint)
                && !string.IsNullOrEmpty(evidence.ResumeReason) => new(source.Identity, HandoffPhase.Failed, 2, evidence.ResumeReason, evidence),
            _ => null
        };
    }
}
