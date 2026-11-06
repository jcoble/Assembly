using System.Text.Json;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Providers;

internal sealed record ProviderProbe(ProbeProviderEffect Payload, int Attempt, string State)
{
    internal static ProviderProbe? Read(IReadScope scope, ProviderState provider, string effectId,
        int? callbackAttempt = null)
    {
        if (provider.Target.Id != provider.Id || effectId != $"probe_provider/{provider.Id}/{provider.Version}") return null;
        var row = scope.QueryFirst("""
            SELECT machine,kind,session_id,run_id,payload_json,attempt,state,order_key FROM engine_outbox WHERE id=$id
            """, r => new
        {
            Machine = r.Text(0),
            Kind = r.Text(1),
            Sessionless = r.IsNull(2) && r.IsNull(3),
            Json = r.Text(4),
            Attempt = r.Long(5),
            State = r.Text(6),
            Order = r.Text(7)
        }, new SqlArg("$id", effectId));
        if (row is null || row.Machine != provider.Machine || row.Kind != "probe_provider" || !row.Sessionless ||
            row.Order != "provider/" + provider.Id || row.Attempt < 0 || row.Attempt > int.MaxValue) return null;
        var stateValid = row.State == "pending" || row.Attempt > 0 && row.State is "inflight" or "uncertain" or "unavailable" or "done" or "failed" or "dead";
        if (!stateValid || callbackAttempt is { } claim && (claim <= 0 || row.Attempt != claim || row.State != "inflight")) return null;
        try
        {
            var payload = JsonSerializer.Deserialize(row.Json, EngineJson.Default.ProbeProviderEffect);
            if (payload is null || payload.TargetId != provider.Id || payload.ProviderVersion != provider.Version ||
                payload.PriorAuthState is not (null or "signed_in")) return null;
            return new(payload, (int)row.Attempt, row.State);
        }
        catch (JsonException) { return null; }
    }

    internal static bool HasPositiveFacts(IReadScope scope, string target) => scope.QueryFirst("""
        SELECT COUNT(*) FROM engine_provider WHERE id=$id AND json_extract(facts_json,'$.auth')='signedIn'
        """, r => r.Long(0), new SqlArg("$id", target)) == 1;
}
