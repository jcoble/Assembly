using System.Globalization;
using System.Text;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Providers;

/// <summary>The closed local auth confirmation shape, shared by target admission and its control branch.</summary>
internal sealed partial record AuthNeed(string Id, long Version, string SessionId, ConfiguredProviderTarget Target,
    long Epoch, string? ParentId, string? Addressee)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string Copy(string provider, string machine) => provider + " needs you to sign in on " + machine + ".";
    internal static string InsertionCopy(string provider, string machine) => Copy(provider, machine) +
        " Your last message reached " + provider + " before sign-in was needed and was not sent again.";

    internal static IReadOnlyList<EventDraft> FailDismissedMessages(IWriteScope scope, CommandContext context, string sessionId) =>
        scope.Query($"""
            UPDATE engine_message AS m SET state='failed',reason='sign_in_dismissed',version=version+1,updated_ms=$now
            WHERE m.machine=$machine AND m.to_session_id=$session AND m.state IN ('queued','held')
              AND m.reason='auth_required' AND m.in_flight=0 AND {MessageExpiry.ReservationFree}
            RETURNING id,version,from_session_id
            """, r => new EventDraft("message.changed", "message", r.Text(0), r.Long(1), false,
                new(sessionId, null, null, r.IsNull(2) ? null : r.Text(2), "failed", context.Epoch, "sign_in_dismissed")),
            new("$now", context.NowMs), new("$machine", context.Machine), new("$session", sessionId));

    internal static AuthNeed? Open(IReadScope scope, string machine, string id)
    {
        var row = scope.QueryFirst("""
            SELECT n.id,n.version,n.session_id,se.provider,se.account_context,se.epoch,se.parent_id,n.addressee_session_id,CAST(n.text AS BLOB)
            FROM engine_needs_me n JOIN engine_session se ON se.id=n.session_id
            WHERE n.id=$id AND n.machine=$machine AND se.machine=$machine AND se.holder='local' AND se.reserved_epoch IS NULL
              AND se.state NOT IN ('foreign','handing_off','ended') AND n.state='open' AND n.kind='confirmation'
              AND n.provider_request_id IS NULL AND n.chat_row_id IS NULL
              AND json_type(n.options_json)='array' AND json_array_length(n.options_json)=2
              AND json_extract(n.options_json,'$[0]')='sign_in' AND json_extract(n.options_json,'$[1]')='dismiss'
            """, r => new
        {
            Need = new AuthNeed(r.Text(0), r.Long(1), r.Text(2), new(r.Text(3), r.Text(4)), r.Long(5),
                r.IsNull(6) ? null : r.Text(6), r.IsNull(7) ? null : r.Text(7)),
            Text = Utf8.GetString(r.Blob(8))
        }, new SqlArg("$id", id), new("$machine", machine));
        if (row is null || row.Text != Copy(row.Need.Target.Provider, machine) && row.Text != InsertionCopy(row.Need.Target.Provider, machine)) return null;
        return ValidId(row.Need.Id, row.Need.SessionId) ? row.Need : null;
    }

    private static bool ValidId(string id, string sessionId)
    {
        var prefix = "sign-in/" + sessionId + "/";
        return id.StartsWith(prefix, StringComparison.Ordinal) &&
            long.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}
