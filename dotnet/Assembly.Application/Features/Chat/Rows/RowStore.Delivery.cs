using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

public sealed record UserDeliveryRow(long Seq, string? RunId, string Text, string? From,
    string ClientId, UserAcknowledgment? Acknowledgment, string? SenderLabel = null,
    AuthSendBlocker? AuthBlocker = null, AuthRetryExecution? RetryExecution = null);

public sealed partial class RowStore
{
    internal UserDeliveryRow? UserDeliveryOfClient(string sessionId, string clientId)
    {
        using var scope = _db.OpenSnapshot(CancellationToken.None);
        return UserDeliveryOfClient(scope, sessionId, clientId);
    }

    public UserDeliveryRow? UserDeliveryOfClient(IReadScope scope, string sessionId, string clientId)
    {
        var rows = scope.Query("""
            SELECT seq,run_id,payload FROM rows INDEXED BY rows_user_client
            WHERE session_id=$session AND json_extract(payload,'$.clientId')=$client AND kind='user' AND item_id IS NULL
            ORDER BY seq DESC LIMIT 2;
            """, row =>
        {
            var payload = JsonSerializer.Deserialize(row.Text(2), RowJsonContext.Rows.UserPayload)
                ?? throw new CallFailed("delivery_ack_conflict");
            return new UserDeliveryRow(row.Long(0), row.IsNull(1) ? null : row.Text(1), payload.Text,
                payload.From, payload.ClientId!, payload.Acknowledgment, payload.SenderLabel, payload.AuthBlocker, payload.RetryExecution);
        }, new SqlArg("$session", sessionId), new SqlArg("$client", clientId));
        if (rows.Count > 1) throw new CallFailed("delivery_ack_conflict");
        return rows.Count == 0 ? null : rows[0];
    }

    public void AcknowledgeUser(IWriteScope scope, string sessionId, string clientId, UserAcknowledgment acknowledgment)
    {
        if (!Positive(acknowledgment)) throw new CallFailed("delivery_ack_conflict");
        var row = UserDeliveryOfClient(scope, sessionId, clientId) ?? throw new CallFailed("delivery_ack_conflict");
        if (row.Acknowledgment == acknowledgment) return;
        if (row.Acknowledgment is not null) throw new CallFailed("delivery_ack_conflict");
        scope.Execute("""
            UPDATE rows SET payload=json_set(payload,'$.acknowledgment',json($ack))
            WHERE session_id=$session AND seq=$seq;
            """, new("$ack", JsonSerializer.Serialize(acknowledgment, RowJsonContext.Rows.UserAcknowledgment)),
            new("$session", sessionId), new("$seq", row.Seq));
    }

    public static void BindAuthBlocker(IWriteScope scope, string sessionId, string clientId,
        long userRowSeq, string userRunId, AuthSendBlocker binding)
    {
        var row = scope.QueryFirst("""
            SELECT run_id,payload FROM rows WHERE session_id=$session AND seq=$seq AND kind='user' AND item_id IS NULL
            """, r => new { Run = r.IsNull(0) ? null : r.Text(0), Payload = JsonSerializer.Deserialize(r.Text(1), RowJsonContext.Rows.UserPayload) },
            new SqlArg("$session", sessionId), new("$seq", userRowSeq));
        if (row is null || row.Run != userRunId || row.Payload is null || row.Payload.ClientId != clientId ||
            row.Payload.Acknowledgment is not null && !Positive(row.Payload.Acknowledgment) ||
            row.Payload.AuthBlocker is { } old && old != binding) throw new CallFailed("auth_binding_conflict");
        if (row.Payload.AuthBlocker == binding) return;
        scope.Execute("""
            UPDATE rows SET payload=json_set(payload,'$.authBlocker',json($binding)) WHERE session_id=$session AND seq=$seq
            """, new("$binding", JsonSerializer.Serialize(binding, RowJsonContext.Rows.AuthSendBlocker)),
            new("$session", sessionId), new("$seq", userRowSeq));
    }

    public static bool Positive(UserAcknowledgment? acknowledgment) => acknowledgment switch
    {
        { Kind: "prompt_inserted", ProviderMessageId: { } id } => !string.IsNullOrWhiteSpace(id),
        { Kind: "steer_injected", ProviderMessageId: null } => true,
        _ => false
    };
}
