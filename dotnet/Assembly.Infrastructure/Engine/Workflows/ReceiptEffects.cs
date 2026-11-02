using Assembly.Engine.Effects;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Infrastructure.Engine.Workflows;

public sealed class ReceiptEffects(IReceiptChecks checks) : IEngineEffects
{
    public Task<EffectReceipt> ExecuteAsync(Effect effect, CancellationToken cancellationToken) =>
        checks.CheckAsync(effect, false, cancellationToken);
    public Task<EffectReceipt> ReconcileAsync(Effect effect, CancellationToken cancellationToken) =>
        checks.CheckAsync(effect, true, cancellationToken);
}
