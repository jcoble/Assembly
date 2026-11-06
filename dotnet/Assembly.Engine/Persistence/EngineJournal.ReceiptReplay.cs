using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    // Read-only validation of this journal's original normal receipt; no reconstruction or secondary authority.
    internal static bool HasExactReceipt(IReadScope scope, CommandContext context, string canonical)
    {
        var receipt = scope.QueryFirst("SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$id",
            r => (Fingerprint: r.Text(0), Result: r.Text(1)), new SqlArg("$id", context.CommandId.ToString("N")));
        if (receipt.Fingerprint != Fingerprint(context, canonical)) return false;
        try { return JsonSerializer.Deserialize(receipt.Result, EngineJson.Default.CommandResult)?.Applied == true; }
        catch (JsonException) { return false; }
    }
}
