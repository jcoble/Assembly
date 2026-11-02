using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;

namespace Assembly.Infrastructure.Engine;

public sealed partial class EngineFeed
{
    public void Replied(IWriteScope scope, SessionObservation observed, SessionTurnReply final)
    {
        if (scope.QueryFirst("SELECT COUNT(*) FROM engine_session WHERE id=$id AND parent_id IS NOT NULL",
            r => r.Long(0), new SqlArg("$id", observed.SessionId)) == 0) return;
        var source = RowStore.TerminalReply(scope, observed.SessionId, final.ChatRunId);
        if (source is null || source.EndedAt != final.EndedAt) throw new CallFailed("turn_reply_changed");
        RequireReply(runtime.Commands.ApplyTurnReply(scope, EngineCommands.PrepareTurnReply(observed, final, source), time.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    public void Recovered(IWriteScope scope, RecoveredChatTurn recovered)
    {
        // Keep ordinary/native display locality in RunRecovery. Only an actual standalone owning helper can originate C6.
        var epoch = scope.QueryFirst("""
            SELECT e.epoch FROM engine_session e JOIN session_info i ON i.session_id=e.id
            WHERE e.id=$id AND e.machine=$machine AND e.holder='local' AND e.reserved_epoch IS NULL
                AND e.state NOT IN ('foreign','handing_off') AND e.parent_id IS NOT NULL
                AND i.helper=1 AND COALESCE(i.machine,'')='' AND i.parent_session_id=e.parent_id
            """, r => (long?)r.Long(0), new SqlArg("$id", recovered.SessionId), new SqlArg("$machine", runtime.Machine));
        if (epoch is null) return;
        var source = RowStore.TerminalReply(scope, recovered.SessionId, recovered.ChatRunId) ?? throw new CallFailed("turn_reply_changed");
        var prepared = EngineCommands.PrepareRecoveredTurnReply(recovered, source, runtime.Machine, epoch.Value);
        RequireReply(runtime.Commands.ApplyRecoveredTurnReply(scope, prepared, time.GetUtcNow().ToUnixTimeMilliseconds()));
    }
    private static void RequireReply(CommandResult result)
    {
        if (!result.Applied) throw new CallFailed(result.Error ?? "turn_reply_refused", result.Error);
    }
}
