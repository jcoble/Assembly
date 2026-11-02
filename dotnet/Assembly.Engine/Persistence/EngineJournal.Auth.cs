using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    internal static bool IsAcceptedAuthTerminal(IReadScope scope, string machine, string sessionId, long terminalSeq, AuthTerminalProof proof)
    {
        var context = AuthTransitions.ReportContext(machine, 0, proof);
        var command = new ReportSignInNeeded(sessionId, terminalSeq, proof);
        var canonical = JsonSerializer.Serialize<EngineCommand>(command, EngineJson.Default.EngineCommand);
        var receipt = scope.QueryFirst("SELECT fingerprint,result_json FROM engine_command_receipt WHERE command_id=$id",
            r => new { Fingerprint = r.Text(0), Result = r.Text(1) }, new SqlArg("$id", proof.ObservationId.ToString("N")));
        if (receipt is null || receipt.Fingerprint != Fingerprint(context, canonical)) return false;
        try
        {
            return JsonSerializer.Deserialize(receipt.Result, EngineJson.Default.CommandResult)?.Applied == true &&
                AuthTerminalRow.Matches(scope, sessionId, terminalSeq, proof);
        }
        catch (JsonException) { return false; }
    }
}
