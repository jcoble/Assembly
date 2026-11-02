using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Domain.Rows;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Providers;

internal sealed partial record AuthNeed
{
    // Only this structural need and the actual matching operation can settle a retained refusal.
    internal static (AuthNeed Need, string State)? Refusal(IReadScope scope, string machine, string session,
        ConfiguredProviderTarget target, long epoch, AuthSendBlocker blocker)
    {
        var row = scope.QueryFirst("""
            SELECT n.version,n.state,n.answer_json,n.answered_by,se.parent_id,n.addressee_session_id,CAST(n.text AS BLOB)
            FROM engine_needs_me n JOIN engine_session se ON se.id=n.session_id
            WHERE n.id=$id AND n.machine=$machine AND n.session_id=$session AND se.machine=$machine
              AND se.holder='local' AND se.reserved_epoch IS NULL AND se.epoch=$epoch
              AND se.provider=$provider AND se.account_context=$account AND se.state NOT IN ('foreign','handing_off','ended')
              AND n.kind='confirmation' AND n.provider_request_id IS NULL AND n.chat_row_id IS NULL
              AND json_type(n.options_json)='array' AND json_array_length(n.options_json)=2
              AND json_extract(n.options_json,'$[0]')='sign_in' AND json_extract(n.options_json,'$[1]')='dismiss'
            """, r => new
        {
            Version = r.Long(0),
            State = r.Text(1),
            Answer = r.IsNull(2) ? null : r.Text(2),
            Actor = r.IsNull(3) ? null : r.Text(3),
            Parent = r.IsNull(4) ? null : r.Text(4),
            Addressee = r.IsNull(5) ? null : r.Text(5),
            Text = Utf8.GetString(r.Blob(6))
        },
            new SqlArg("$id", blocker.NeedId), new("$machine", machine), new("$session", session), new("$epoch", epoch),
            new("$provider", target.Provider), new("$account", target.AccountContext));
        if (row is null || !ValidId(blocker.NeedId, session) || blocker.NeedVersionAtBlock <= 0 || row.Version < blocker.NeedVersionAtBlock ||
            row.Text != Copy(target.Provider, machine) && row.Text != InsertionCopy(target.Provider, machine)) return null;
        if (row.State == "answered")
        {
            const string prefix = "sign-in/";
            if (row.Version <= blocker.NeedVersionAtBlock || row.Answer is null || JsonSerializer.Deserialize(row.Answer, EngineJson.Default.String) != "signed_in" ||
                row.Actor is null || !row.Actor.StartsWith(prefix, StringComparison.Ordinal) || row.Actor.Length == prefix.Length ||
                scope.QueryFirst("""
                    SELECT COUNT(*) FROM engine_sign_in WHERE id=$id AND machine=$machine AND provider=$provider
                      AND account_context=$account AND state='signed_in'
                    """, r => r.Long(0), new SqlArg("$id", row.Actor[prefix.Length..]), new("$machine", machine),
                    new("$provider", target.Provider), new("$account", target.AccountContext)) != 1) return null;
        }
        else if (row.State == "dismissed") { if (row.Version <= blocker.NeedVersionAtBlock || row.Answer is not null) return null; }
        else if (row.State != "open") return null;
        return (new(blocker.NeedId, row.Version, session, target, epoch, row.Parent, row.Addressee), row.State);
    }
}
