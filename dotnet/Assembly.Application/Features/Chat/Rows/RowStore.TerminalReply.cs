using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

public sealed record FinalizedChatReply(string SessionId, string ChatRunId,
    long TerminalSeq, string TerminalState, long EndedAt, long? MessageSeq, string Text,
    long? ErrorSeq, string? Error);

public sealed partial class RowStore
{
    private static readonly UTF8Encoding ReplyUtf8 = new(false, true);

    internal FinalizedChatReply? TerminalReply(string sessionId, string chatRunId)
    {
        using var scope = _db.OpenSnapshot(CancellationToken.None);
        return TerminalReply(scope, sessionId, chatRunId);
    }

    public static FinalizedChatReply? TerminalReply(IReadScope scope, string sessionId, string chatRunId)
    {
        var terminal = scope.QueryFirst("""
            SELECT seq FROM rows WHERE session_id=$session AND run_id=$run AND kind='turn' ORDER BY seq DESC LIMIT 1
            """, r => (long?)r.Long(0), new("$session", sessionId), new("$run", chatRunId));
        return terminal is { } seq ? TerminalReplyAt(scope, sessionId, chatRunId, seq) : null;
    }

    public static FinalizedChatReply? TerminalReplyAt(IReadScope scope, string sessionId, string chatRunId, long terminalSeq)
    {
        var terminal = scope.QueryFirst("""
            SELECT CAST(payload AS BLOB),created_at FROM rows
            WHERE session_id=$session AND seq=$seq AND run_id=$run AND kind='turn'
            """, r => new { Payload = ReplyJson(r.Blob(0)), At = r.Long(1) },
            new("$session", sessionId), new("$run", chatRunId), new("$seq", terminalSeq));
        if (terminal is null) return null;
        var turn = terminal.Payload.Deserialize(RowJsonContext.Rows.TurnPayload) ?? throw new JsonException("Missing terminal turn.");
        if (turn.State == "started") return null;
        if (turn.State is not ("finished" or "cancelled" or "failed")) throw new JsonException("Invalid terminal state.");
        var message = ReplyRow(scope, sessionId, chatRunId, terminalSeq, RowKind.Message);
        var error = ReplyRow(scope, sessionId, chatRunId, terminalSeq, RowKind.Error);
        return new(sessionId, chatRunId, terminalSeq, turn.State, terminal.At, message?.Seq,
            message is null ? "" : PlainText.Of(message.Payload.GetProperty("blocks")), error?.Seq,
            error?.Payload.Deserialize(RowJsonContext.Rows.ErrorPayload)?.Message);
    }

    private sealed record ReplySourceRow(long Seq, JsonElement Payload);
    private static ReplySourceRow? ReplyRow(IReadScope scope, string session, string run, long through, string kind) => scope.QueryFirst("""
        SELECT seq,CAST(payload AS BLOB) FROM rows
        WHERE session_id=$session AND run_id=$run AND kind=$kind AND seq<=$through ORDER BY seq DESC LIMIT 1
        """, r => new ReplySourceRow(r.Long(0), ReplyJson(r.Blob(1))),
        new("$session", session), new("$run", run), new("$kind", kind), new("$through", through));
    private static JsonElement ReplyJson(byte[] bytes) => JsonSerializer.Deserialize(ReplyUtf8.GetString(bytes), RowJsonContext.Rows.JsonElement);
}
