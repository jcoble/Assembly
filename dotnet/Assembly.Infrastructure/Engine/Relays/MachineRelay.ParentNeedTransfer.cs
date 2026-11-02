using Assembly.Engine.Hosting;
using Assembly.Engine.NeedsMe;

namespace Assembly.Infrastructure.Engine.Relays;

internal sealed partial class MachineRelay
{
    private Task<NeedAnswerAck> ProbeParentNeed(Endpoint target, NeedAnswerRequestDescriptor descriptor, CancellationToken ct) =>
        target.Remote is null ? Task.FromResult(_engine.Read(scope => _runtime.Commands.ReadForwardedParentNeed(scope, descriptor), ct))
            : _forward.ProbeParentNeedAsync(target.Remote, target.Live!.OwnerBinding, descriptor, ct, target.Live);

    private async Task<NeedAnswerAck> ExecuteParentNeed(Endpoint target, ParentNeedPrepared original, CancellationToken ct)
    {
        await target.RequireCurrent(ct);
        if (target.Remote is null)
        {
            var recorded = _engine.Read(scope => _runtime.Commands.ReadForwardedParentNeed(scope, original.Descriptor), ct);
            if (recorded.Result is not null) return recorded;
            var prepared = _needs.PrepareForwardedParentNeed(original, ct);
            return _engine.Write(scope =>
            {
                _runtime.Commands.ApplyForwardedParentNeed(scope, prepared, Now);
                return _runtime.Commands.ReadForwardedParentNeed(scope, original.Descriptor);
            });
        }
        await using var lease = await target.Remote.AcquireRelayBulkAsync(target.Live!.OwnerBinding, ct, target.Live);
        try { return await _forward.ExecuteParentNeedAsync(lease, original, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            Diagnostic(error);
            await target.RequireCurrent(ct);
            // Never retransmit an execute after an uncertain response, even when the receipt is missing.
            return await ProbeParentNeed(target, original.Descriptor, ct);
        }
    }
}
