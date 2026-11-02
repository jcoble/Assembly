using System.Text.Json;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Database;
using Assembly.Engine.Reading;

namespace Assembly.Engine.Mirror;

/// <summary>Validates opaque identity display copies without running owner transitions.</summary>
internal static class AuthMirrorIdentity
{
    internal static void ValidateRow(JsonElement row, string machine)
    {
        if (!row.TryGetProperty("entityKind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("provider" or "sign_in")) return;
        if (!Text(row, "accountContext", out var account) || !AccountContext.IsValid(account) ||
            !Text(row, "provider", out var provider) || string.IsNullOrEmpty(provider) || !Text(row, "id", out var id) || string.IsNullOrEmpty(id) ||
            !Text(row, "machine", out var owner) || owner != machine || kind.GetString() == "provider" && id != provider + "/" + account)
            throw new ArgumentException("Invalid mirror row");
    }

    internal static void ValidateGraph(IReadScope scope, string machine, long generation)
    {
        if (scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_mirror_row s WHERE s.machine=$machine AND s.generation=$generation AND s.entity_kind='sign_in'
                AND NOT EXISTS (SELECT 1 FROM engine_mirror_row p WHERE p.machine=s.machine AND p.generation=s.generation
                    AND p.entity_kind='provider' AND p.entity_id=json_extract(s.row_json,'$.provider')||'/'||json_extract(s.row_json,'$.accountContext'))
            """, r => r.Long(0), new SqlArg("$machine", machine), new("$generation", generation)) != 0)
            throw new ArgumentException("Invalid mirror row");
    }

    internal static void ValidateEvents(IReadScope scope, string machine, long generation, long after, long through)
    {
        if (scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_event e WHERE e.machine=$machine AND e.seq>$after AND e.seq<=$through
                AND e.entity_kind IN ('provider','sign_in') AND NOT EXISTS
                    (SELECT 1 FROM engine_mirror_row r WHERE r.machine=e.machine AND r.generation=$generation AND r.entity_kind=e.entity_kind AND r.entity_id=e.entity_id)
            """, r => r.Long(0), new SqlArg("$machine", machine), new("$generation", generation), new("$after", after), new("$through", through)) != 0)
            throw new ArgumentException("Invalid mirror event");
    }

    internal static void ValidateCursor(IReadScope scope, string machine, long generation, CursorAt? at)
    {
        if (at?.Kind is not ("provider" or "sign_in")) return;
        if (scope.QueryFirst("""
            SELECT COUNT(*) FROM engine_mirror_row WHERE machine=$machine AND generation=$generation AND entity_kind=$kind AND entity_id=$id
            """, r => r.Long(0), new SqlArg("$machine", machine), new("$generation", generation), new("$kind", at.Kind), new("$id", at.EntityId)) != 1)
            throw new ArgumentException("Invalid mirror cursor");
    }

    private static bool Text(JsonElement row, string name, out string? value)
    {
        value = null;
        if (!row.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString(); return true;
    }
}
