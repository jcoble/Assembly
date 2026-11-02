using Assembly.Engine.Effects;

namespace Assembly.Engine.Workflows.Workspaces;

public interface IWorkflowWorkspace
{
    Task<EffectReceipt> PrepareAsync(Effect effect, bool reconcile, CancellationToken cancellationToken);
}
