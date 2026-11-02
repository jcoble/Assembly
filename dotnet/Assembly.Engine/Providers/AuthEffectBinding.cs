using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Effects;

namespace Assembly.Engine.Providers;

internal static class AuthEffectBinding
{
    internal static bool IsAuth(string kind) => kind is "probe_provider" or "begin_sign_in" or "cancel_sign_in";

    internal static bool ValidConfirmation(IReadScope scope, CommandContext context, Effect effect, EffectReceipt receipt)
    {
        if (effect.SessionId is not null || effect.RunId is not null || context.Epoch != 0 ||
            scope.QueryFirst("SELECT machine FROM engine_outbox WHERE id=$id", r => r.Text(0), new SqlArg("$id", effect.Id)) != context.Machine)
            return false;
        try
        {
            if (effect.Kind == "probe_provider")
            {
                var intent = effect.Payload.Deserialize(EngineJson.Default.ProbeProviderEffect);
                var data = receipt.Data.Deserialize(EngineJson.Default.ProviderEffectData);
                if (intent is null || intent.ProviderVersion <= 0 || intent.PriorAuthState is not (null or "signed_in") || data != new ProviderEffectData(intent.TargetId, intent.ProviderVersion) ||
                    effect.Id != $"probe_provider/{intent.TargetId}/{intent.ProviderVersion}" || effect.OrderKey != "provider/" + intent.TargetId) return false;
                var provider = ProviderState.Read(scope, intent.TargetId);
                return provider is not null && provider.Machine == context.Machine && provider.Target.Id == intent.TargetId && provider.Version >= intent.ProviderVersion;
            }
            var operationId = effect.Kind switch
            {
                "begin_sign_in" => effect.Payload.Deserialize(EngineJson.Default.BeginSignInEffect)?.OperationId,
                "cancel_sign_in" => effect.Payload.Deserialize(EngineJson.Default.CancelSignInEffect)?.OperationId,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(operationId) || effect.Id != effect.Kind + "/" + operationId) return false;
            var operation = scope.QueryFirst("""
                SELECT machine,provider,account_context,method_id,state FROM engine_sign_in WHERE id=$id
                """, r => new { Machine = r.Text(0), Provider = r.Text(1), Account = r.Text(2), Method = r.IsNull(3) ? null : r.Text(3), State = r.Text(4) }, new SqlArg("$id", operationId));
            if (operation is null || operation.Machine != context.Machine) return false;
            var target = new ConfiguredProviderTarget(operation.Provider, operation.Account);
            if (effect.OrderKey != "provider/" + target.Id) return false;
            var providerRow = ProviderState.Read(scope, target.Id);
            if (providerRow is null || providerRow.Machine != context.Machine || providerRow.Target != target) return false;
            var result = receipt.Data.Deserialize(EngineJson.Default.SignInEffectData);
            if (effect.Kind == "begin_sign_in")
                return result == new SignInEffectData(operationId, "started") &&
                    effect.Payload.Deserialize(EngineJson.Default.BeginSignInEffect) == new BeginSignInEffect(operationId, operation.Provider, operation.Method);
            return result == new SignInEffectData(operationId, "stopped") && operation.State is "cancelled" or "expired" or "failed";
        }
        catch (JsonException) { return false; }
    }
}
