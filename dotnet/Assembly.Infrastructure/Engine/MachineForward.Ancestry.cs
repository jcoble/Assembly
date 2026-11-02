using System.IO;
using System.Text.Json;
using Assembly.Engine;
using Assembly.Application.Common.Machines;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineForward
{
    internal async Task<CommandResult> ForeignAncestryAsync(string machine, ForeignAncestryArgs ancestry, CancellationToken ct)
    {
        if (!FitsMachineFrame("foreign_ancestry", ancestry, EngineSocketJson.Default.ForeignAncestryArgs))
        {
            var owner = byMachineId(machine) ?? throw new MachineUnreachable(machine, "Remote engine machine is disconnected");
            var binding = await owner.CaptureOwnerBindingAsync(machine, ct);
            await using var lease = await owner.AcquireRelayBulkAsync(binding, ct);
            return TitleResult(await ForeignAncestryTitlesAsync(lease, ancestry, ct),
                ForeignTitleDigests.Structure(machine, ancestry.FromMachine, ancestry.ParentSessionId, ancestry.RootToParent));
        }
        var args = JsonSerializer.SerializeToElement(ancestry, EngineSocketJson.Default.ForeignAncestryArgs);
        var result = await CallAsync(machine, "engine", "foreign_ancestry", args, false, ct);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid foreign ancestry result");
    }
}
