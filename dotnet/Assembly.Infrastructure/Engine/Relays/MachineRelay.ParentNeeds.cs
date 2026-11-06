using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Infrastructure.Remote;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class MachineRelay
{
    private async Task RelayParentNeeds(IReadOnlyDictionary<string, Endpoint> endpoints, CancellationToken ct)
    {
        var sources = endpoints.Values.Prepend(EndpointFor(_runtime.Machine, endpoints)).OrderBy(owner => owner.Machine, StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ParentNeedCursor? after = null, cut = null;
            do
            {
                await source.RequireCurrent(ct);
                var args = new ParentNeedPendingArgs(after, cut);
                var page = source.Remote is null
                    ? _runtime.Reader.ParentNeedPending(args, value => JsonSerializer.SerializeToUtf8Bytes(value, EngineJson.Default.ParentNeedPendingPage).Length, ct)
                    : await _forward.ParentNeedPendingAsync(source.Remote, source.Live!.OwnerBinding, args, ct, source.Live);
                await source.RequireCurrent(ct);
                foreach (var item in page.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await RelayParentNeed(source, item.IntentId, endpoints, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) { Diagnostic(error); }
                }
                if (!page.More) break;
                if (page.Next is null || page.Cut is null || page.Next == after) throw new IOException("Invalid parent need census cursor");
                after = page.Next; cut = page.Cut;
            } while (true);
        }
    }

    private async Task RelayParentNeed(Endpoint source, string intentId, IReadOnlyDictionary<string, Endpoint> endpoints, CancellationToken ct)
    {
        var stage = "source_read";
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, source.Live?.Invalidated ?? default);
            ct = stop.Token;
            await source.RequireCurrent(ct);
            await using var sourceLease = source.Remote is null ? null : await source.Remote.AcquireRelayBulkAsync(source.Live!.OwnerBinding, ct, source.Live);
            var capture = await ReadNeedSource(source, sourceLease, intentId, ct);
            var payload = capture.ReadPayload();
            if (payload.Phase == ParentNeedPhase.Outcome) return;
            Endpoint? target = null;
            if (payload.Phase == ParentNeedPhase.Raw)
            {
                ParentNeedCaptured? found = null;
                var completeCensus = _engine.Read(scope => scope.QueryFirst("SELECT COUNT(*) FROM saved_machines", row => row.Long(0)), ct) == endpoints.Count;
                foreach (var candidate in endpoints.Values.Prepend(EndpointFor(_runtime.Machine, endpoints)).Where(owner => owner.Machine != source.Machine))
                {
                    await candidate.RequireCurrent(ct);
                    stage = "target_capture:" + candidate.Machine;
                    var captured = candidate.Remote is null ? _needs.CaptureParentNeed(payload.Source, ct)
                        : await _forward.CaptureParentNeedAsync(candidate.Remote, candidate.Live!.OwnerBinding, payload.Source, ct, candidate.Live);
                    await candidate.RequireCurrent(ct);
                    if (captured.Error == "bad_args") continue;
                    if (captured.Error is not null || captured.Target is null) { completeCensus = false; continue; }
                    if (found is not null) throw new IOException("Ambiguous parent need target owner");
                    found = captured; target = candidate;
                }
                if (found is null)
                {
                    if (completeCensus) await WriteNeedOutcome(source, capture,
                        new("source", "refused", payload.InputSha256, null, null, null, "bad_args"), ct);
                    return;
                }
                stage = "prepare_bytes";
                var request = payload.Input with { Answer = PreparedParentNeedPhase.NormalizeAnswer(payload.Input, found.Target!) };
                var prepared = EngineCommands.PrepareParentNeed(payload.Source.OperationId, request, found.Target!, found.Binding, ct);
                var descriptor = new NeedAnswerRequestDescriptor(payload.Source, found.Target!, prepared.Manifest, found.Binding);
                var installed = new ParentNeedPrepared(descriptor, prepared.AnswerUtf8);
                await source.RequireCurrent(ct); await target!.RequireCurrent(ct);
                stage = "source_prepare";
                var sourcePreparation = PreparedParentNeedPhase.Prepare(capture, installed, ct);
                stage = "source_prepare_rpc";
                var result = source.Remote is null
                    ? _engine.Write(scope => _runtime.Commands.PrepareParentNeedIntent(scope, sourcePreparation, Now))
                    : await _forward.PrepareParentNeedAsync(sourceLease!, intentId, capture.Version, installed, ct);
                if (!result.Applied)
                {
                    await WriteNeedOutcome(source, capture, new("source", "refused", payload.InputSha256, null, null, null, result.Error), ct, descriptor);
                    return;
                }
                stage = "source_prepared_read";
                capture = await ReadNeedSource(source, sourceLease, intentId, ct); payload = capture.ReadPayload();
            }
            if (payload.Prepared is not { } original) return;
            var targetMachine = original.Descriptor.Target.ChildOwnerMachine;
            if (targetMachine == _runtime.Machine) target = EndpointFor(targetMachine, endpoints);
            else if (!endpoints.TryGetValue(targetMachine, out target)) return;
            using var targetStop = CancellationTokenSource.CreateLinkedTokenSource(ct, target.Live?.Invalidated ?? default);
            ct = targetStop.Token;
            await source.RequireCurrent(ct); await target.RequireCurrent(ct);
            var execute = payload.Phase == ParentNeedPhase.Prepared;
            if (execute)
            {
                stage = "source_dispatch";
                var phase = PreparedParentNeedPhase.Dispatch(capture, ParentNeedCodec.Identity(original.Descriptor), ct);
                // Only this writer's fresh Prepared→Executing transition permits execute; receipt replay permits probe only.
                var dispatched = source.Remote is null
                    ? _engine.Write(scope => _runtime.Commands.DispatchParentNeedWithAck(scope, phase, Now))
                    : await _forward.DispatchParentNeedAsync(source.Remote, source.Live!.OwnerBinding,
                        new(intentId, capture.Version, ParentNeedCodec.Identity(original.Descriptor)), ct, source.Live);
                if (!dispatched.Result.Applied)
                {
                    await WriteNeedOutcome(source, capture, new("source", "refused", ParentNeedCodec.Identity(original.Descriptor), null, null, null, dispatched.Result.Error), ct);
                    return;
                }
                execute = dispatched.Dispatched;
                capture = await ReadNeedSource(source, sourceLease, intentId, ct);
            }
            stage = execute ? "target_execute" : "target_probe";
            var ack = execute ? await ExecuteParentNeed(target, original, ct) : await ProbeParentNeed(target, original.Descriptor, ct);
            await source.RequireCurrent(ct); await target.RequireCurrent(ct);
            if (ack.IdentitySha256 != ParentNeedCodec.Identity(original.Descriptor)) throw new IOException("Parent need receipt identity changed");
            var outcome = ack.Status is "recorded" or "refused"
                ? new ParentNeedOutcome("target", ack.Status, ack.IdentitySha256, ack.Result, ack.EffectId, ack.EffectState, null)
                : new("target", "unknown", ack.IdentitySha256, null, null, null, "unknown");
            stage = "source_outcome";
            await WriteNeedOutcome(source, capture, outcome, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        { throw new IOException("parent_need " + stage + ": " + error.Message, error); }
    }


    private Task<ParentNeedSourceCapture> ReadNeedSource(Endpoint source, RemoteMachine.RelayBulkLease? lease, string intentId, CancellationToken ct) =>
        source.Remote is null ? Task.FromResult(_runtime.Reader.ParentNeedInput(intentId, ct))
            : _forward.ReadParentNeedInputAsync(lease!, source.Machine, intentId, ct);
    private async Task WriteNeedOutcome(Endpoint source, ParentNeedSourceCapture capture, ParentNeedOutcome outcome, CancellationToken ct,
        NeedAnswerRequestDescriptor? rejectedPreparation = null)
    {
        await source.RequireCurrent(ct);
        var phase = PreparedParentNeedPhase.Complete(capture, outcome, ct, rejectedPreparation);
        var result = source.Remote is null ? _engine.Write(scope => _runtime.Commands.CompleteParentNeedIntent(scope, phase, Now))
            : await _forward.CompleteParentNeedAsync(source.Remote, source.Live!.OwnerBinding, new(capture.IntentId, capture.Version, outcome, rejectedPreparation), ct, source.Live);
        if (!result.Applied) throw new IOException("Parent need source outcome refused: " + result.Error);
    }
}
