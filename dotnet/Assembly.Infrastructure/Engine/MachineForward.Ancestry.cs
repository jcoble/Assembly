using System.IO;
using System.Text.Json;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

internal sealed partial class MachineForward
{
    internal async Task<CommandResult> ForeignAncestryAsync(string machine, ForeignAncestryArgs ancestry, CancellationToken ct)
    {
        var args = JsonSerializer.SerializeToElement(ancestry, EngineSocketJson.Default.ForeignAncestryArgs);
        var result = await CallAsync(machine, "engine", "foreign_ancestry", args, false, ct);
        return result.Deserialize(EngineJson.Default.CommandResult) ?? throw new IOException("Invalid foreign ancestry result");
    }
}
