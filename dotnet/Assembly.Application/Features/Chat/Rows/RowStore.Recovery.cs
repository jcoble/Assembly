using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Chat.Rows;

public sealed partial class RowStore
{
    private static bool RecoveryCurrent(IReadScope scope, RecoveredChatTurn recovered)
    {
        var terminal = scope.QueryFirst("""
            SELECT seq,CAST(payload AS BLOB) FROM rows
            WHERE session_id=$session AND run_id=$run AND kind='turn' ORDER BY seq DESC LIMIT 1
            """, r => new { Seq = r.Long(0), Payload = ReplyJson(r.Blob(1)) },
            new("$session", recovered.SessionId), new("$run", recovered.ChatRunId));
        if (terminal is null) throw new CallFailed("recovery_changed");
        var state = terminal.Payload.GetProperty("state").GetString();
        if (state is "finished" or "failed" or "cancelled") return false;
        var through = scope.QueryFirst("SELECT MAX(seq) FROM rows WHERE session_id=$session AND run_id=$run",
            r => r.Long(0), new("$session", recovered.SessionId), new("$run", recovered.ChatRunId));
        if (state != "started" || terminal.Seq != recovered.StartedTurnSeq || through != recovered.SavedThrough)
            throw new CallFailed("recovery_changed");
        return true;
    }
}
