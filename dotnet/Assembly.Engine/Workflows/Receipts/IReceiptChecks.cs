using Assembly.Engine.Effects;

namespace Assembly.Engine.Workflows.Receipts;

public interface IReceiptChecks
{
    Task<EffectReceipt> CheckAsync(Effect effect, bool reconcile, CancellationToken cancellationToken);
}
