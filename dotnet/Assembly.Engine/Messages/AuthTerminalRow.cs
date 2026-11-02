using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;

namespace Assembly.Engine.Messages;

/// <summary>Exact retained terminal PK and same-execution error proof, independent of later current reservations.</summary>
internal static class AuthTerminalRow
{
    internal static bool Matches(IReadScope scope, string sessionId, long terminalSeq, AuthTerminalProof proof)
    {
        if (terminalSeq <= 0 || !AuthExecution.ClosedProof(proof)) return false;
        try
        {
            var terminal = scope.QueryFirst("""
                SELECT payload FROM rows WHERE session_id=$session AND seq=$seq AND run_id=$run AND kind='turn' AND item_id IS NULL
                """, r => JsonSerializer.Deserialize(r.Text(0), EngineJson.Default.TurnPayload),
                new SqlArg("$session", sessionId), new("$seq", terminalSeq), new("$run", proof.ChatRunId));
            if (terminal is not { State: "failed" } || terminal.AuthFailure != proof) return false;
            var before = scope.QueryFirst("""
                SELECT seq,json_extract(payload,'$.state') FROM rows
                WHERE session_id=$session AND run_id=$run AND kind='turn' AND seq<$seq ORDER BY seq DESC LIMIT 1
                """, r => new { Seq = r.Long(0), State = r.Text(1) },
                new SqlArg("$session", sessionId), new("$run", proof.ChatRunId), new("$seq", terminalSeq));
            if (before is null || before.State != "started") return false;
            return scope.QueryFirst("""
                SELECT COUNT(*) FROM rows WHERE session_id=$session AND run_id=$run AND kind='error' AND item_id IS NULL
                  AND seq>$started AND seq<$seq AND json_extract(payload,'$.code')='-32000'
                """, r => r.Long(0), new SqlArg("$session", sessionId), new("$run", proof.ChatRunId), new("$started", before.Seq), new("$seq", terminalSeq)) == 1 &&
                scope.QueryFirst("""
                    SELECT COUNT(*) FROM rows WHERE session_id=$session AND run_id=$run AND kind='turn'
                      AND json_type(payload,'$.authFailure')='object' AND json_extract(payload,'$.authFailure.observationId')=$observation
                    """, r => r.Long(0), new SqlArg("$session", sessionId), new("$run", proof.ChatRunId), new("$observation", proof.ObservationId.ToString())) == 1;
        }
        catch (JsonException) { return false; }
    }
}
