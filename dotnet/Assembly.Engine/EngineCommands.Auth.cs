using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;
using Assembly.Engine.Persistence;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    public static bool IsAcceptedAuthRetryStarted(IReadScope scope, string sessionId, AuthRetryExecution execution)
    {
        var machine = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0));
        return !string.IsNullOrWhiteSpace(machine) && EngineJournal.IsAcceptedAuthRetryStarted(scope, machine, sessionId, execution);
    }

    public static bool HasPriorAuthTerminal(IReadScope scope, string sessionId, string priorChatRunId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(priorChatRunId)) return false;
        var machine = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0));
        if (string.IsNullOrWhiteSpace(machine)) return false;
        var owner = scope.QueryFirst("SELECT parent_id FROM engine_session WHERE id=$id AND machine=$machine",
            r => new { Parent = r.IsNull(0) ? null : r.Text(0) }, new SqlArg("$id", sessionId), new("$machine", machine));
        if (owner is null) return false;
        try
        {
            var source = Assembly.Application.Features.Chat.Rows.RowStore.TerminalReply(scope, sessionId, priorChatRunId);
            if (source is null || scope.QueryFirst("""
                SELECT COUNT(*) FROM rows WHERE session_id=$session AND run_id=$run AND kind='turn' AND item_id IS NULL
                  AND seq<$terminal AND json_extract(payload,'$.state')='started'
                """, r => r.Long(0), new SqlArg("$session", sessionId), new("$run", priorChatRunId), new("$terminal", source.TerminalSeq)) != 1) return false;
            return owner.Parent is null || EngineJournal.HasPriorAuthReply(scope, machine, source, owner.Parent);
        }
        catch (System.Text.Json.JsonException) { return false; }
        catch (System.Text.DecoderFallbackException) { return false; }
        catch (System.Text.EncoderFallbackException) { return false; }
    }

    public static bool IsAcceptedAuthTerminal(IReadScope scope, string sessionId, long terminalSeq, AuthTerminalProof proof)
    {
        var machine = scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", r => r.Text(0));
        return !string.IsNullOrWhiteSpace(machine) && EngineJournal.IsAcceptedAuthTerminal(scope, machine, sessionId, terminalSeq, proof);
    }
}
