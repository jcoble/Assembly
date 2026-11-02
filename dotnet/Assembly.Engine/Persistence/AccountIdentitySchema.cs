using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Effects;
using Assembly.Engine.Providers;

namespace Assembly.Engine.Persistence;

/// <summary>Checks retained identity provenance before recovery, without rewriting history.</summary>
internal static class AccountIdentitySchema
{
    internal static void Validate(IReadScope scope, bool legacy)
    {
        foreach (var table in new[] { "engine_session", "engine_provider", "engine_sign_in" })
            Require(scope.QueryFirst($"SELECT COUNT(*) FROM {table} WHERE " +
                (legacy ? "account_context<>''" : "account_context<>'' AND (length(account_context)<>72 OR substr(account_context,1,8)<>'acct-v1:' OR substr(account_context,9) GLOB '*[^0-9a-f]*')"), r => r.Long(0)) == 0);
        Require(scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_provider WHERE provider='' OR id<>provider||'/'||account_context
            """, r => r.Long(0)) == 0);
        Require(scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_sign_in s WHERE NOT EXISTS
                (SELECT 1 FROM engine_provider p WHERE p.machine=s.machine AND p.provider=s.provider AND p.account_context=s.account_context AND p.id=s.provider||'/'||s.account_context)
            """, r => r.Long(0)) == 0);
        ValidateMirrors(scope, legacy);
        Require(scope.QueryFirst(Identities + """
            SELECT COUNT(*) FROM engine_event e WHERE e.entity_kind IN ('provider','sign_in') AND NOT EXISTS
                (SELECT 1 FROM identities i WHERE i.machine=e.machine AND i.kind=e.entity_kind AND i.id=e.entity_id)
            """, r => r.Long(0)) == 0);
        Require(scope.QueryFirst(Identities + """
            SELECT COUNT(*) FROM engine_mirror m WHERE m.at_json IS NOT NULL
                AND json_extract(m.at_json,'$.kind') IN ('provider','sign_in') AND NOT EXISTS
                (SELECT 1 FROM identities i WHERE i.machine=m.machine AND i.kind=json_extract(m.at_json,'$.kind') AND i.id=json_extract(m.at_json,'$.entityId'))
            """, r => r.Long(0)) == 0);
        ValidateEffects(scope);
    }

    private static void ValidateMirrors(IReadScope scope, bool legacy)
    {
        Require(scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_mirror_row r WHERE entity_kind IN ('provider','sign_in') AND
                (json_type(row_json,'$.accountContext') IS NOT 'text' OR json_type(row_json,'$.provider') IS NOT 'text'
                 OR json_extract(row_json,'$.provider')='' OR json_extract(row_json,'$.entityKind') IS NOT entity_kind
                 OR json_extract(row_json,'$.id') IS NOT entity_id OR json_extract(row_json,'$.machine') IS NOT machine
                 OR (entity_kind='provider' AND entity_id IS NOT json_extract(row_json,'$.provider')||'/'||json_extract(row_json,'$.accountContext')))
            """, r => r.Long(0)) == 0);
        var invalid = legacy ? "json_extract(row_json,'$.accountContext')<>''" :
            "json_extract(row_json,'$.accountContext')<>'' AND (length(json_extract(row_json,'$.accountContext'))<>72 OR substr(json_extract(row_json,'$.accountContext'),1,8)<>'acct-v1:' OR substr(json_extract(row_json,'$.accountContext'),9) GLOB '*[^0-9a-f]*')";
        Require(scope.QueryFirst("SELECT COUNT(*) FROM engine_mirror_row WHERE entity_kind IN ('provider','sign_in') AND (" + invalid + ")", r => r.Long(0)) == 0);
        Require(scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_mirror_row s WHERE s.entity_kind='sign_in' AND NOT EXISTS
                (SELECT 1 FROM engine_mirror_row p WHERE p.machine=s.machine AND p.generation=s.generation AND p.entity_kind='provider'
                 AND p.entity_id=json_extract(s.row_json,'$.provider')||'/'||json_extract(s.row_json,'$.accountContext'))
            """, r => r.Long(0)) == 0);
    }

    private static void ValidateEffects(IReadScope scope)
    {
        string? after = null;
        while (true)
        {
            var rows = scope.Query("""
                SELECT id,kind,machine,session_id,run_id,order_key,payload_json,receipt_json FROM engine_outbox
                WHERE kind IN ('probe_provider','begin_sign_in','cancel_sign_in') AND ($after IS NULL OR id>$after) ORDER BY id LIMIT 100
                """, r => new { Id = r.Text(0), Kind = r.Text(1), Machine = r.Text(2), Session = r.IsNull(3), Run = r.IsNull(4), Order = r.Text(5), Payload = r.Text(6), Receipt = r.IsNull(7) ? null : r.Text(7) }, new SqlArg("$after", after));
            if (rows.Count == 0) return;
            foreach (var row in rows)
            {
                Require(row.Session && row.Run);
                try
                {
                    var receipt = row.Receipt is null ? null : JsonSerializer.Deserialize(row.Receipt, EngineJson.Default.EffectReceipt);
                    if (row.Kind == "probe_provider")
                    {
                        var probe = JsonSerializer.Deserialize(row.Payload, EngineJson.Default.ProbeProviderEffect);
                        Require(probe is not null && probe.ProviderVersion > 0 && probe.PriorAuthState is null or "signed_in");
                        var provider = ProviderState.Read(scope, probe!.TargetId);
                        Require(provider is not null && provider.Machine == row.Machine && provider.Version >= probe.ProviderVersion &&
                            row.Id == $"probe_provider/{probe.TargetId}/{probe.ProviderVersion}" && row.Order == "provider/" + probe.TargetId);
                        if (receipt is not null && receipt.Data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                            Require(receipt.Data.Deserialize(EngineJson.Default.ProviderEffectData) == new ProviderEffectData(probe.TargetId, probe.ProviderVersion));
                    }
                    else
                    {
                        var begin = row.Kind == "begin_sign_in" ? JsonSerializer.Deserialize(row.Payload, EngineJson.Default.BeginSignInEffect) : null;
                        var id = begin?.OperationId ?? JsonSerializer.Deserialize(row.Payload, EngineJson.Default.CancelSignInEffect)?.OperationId;
                        Require(!string.IsNullOrEmpty(id) && row.Id == row.Kind + "/" + id);
                        var operation = scope.QueryFirst("SELECT machine,provider,account_context,method_id FROM engine_sign_in WHERE id=$id",
                            r => new { Machine = r.Text(0), Provider = r.Text(1), Account = r.Text(2), Method = r.IsNull(3) ? null : r.Text(3) }, new SqlArg("$id", id));
                        Require(operation is not null && operation.Machine == row.Machine && row.Order == "provider/" + operation.Provider + "/" + operation.Account);
                        if (begin is not null) Require(begin == new BeginSignInEffect(id!, operation!.Provider, operation.Method));
                        if (receipt is not null && receipt.Data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                            Require(receipt.Data.Deserialize(EngineJson.Default.SignInEffectData) == new SignInEffectData(id!, row.Kind == "begin_sign_in" ? "started" : "stopped"));
                    }
                }
                catch (JsonException) { throw Failure(); }
            }
            after = rows[^1].Id;
        }
    }

    private static void Require(bool valid) { if (!valid) throw Failure(); }
    private static InvalidOperationException Failure() => new("account_identity_upgrade_required");
    private const string Identities = """
        WITH identities(machine,kind,id) AS (
            SELECT machine,'provider',id FROM engine_provider UNION ALL SELECT machine,'sign_in',id FROM engine_sign_in
            UNION ALL SELECT machine,entity_kind,entity_id FROM engine_mirror_row WHERE entity_kind IN ('provider','sign_in'))
        """;
}
