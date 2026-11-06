using System.Text.Json;

namespace Assembly.Engine.Providers;

/// <summary>The safe catalogue projection only; runnable methods and challenges never cross this boundary.</summary>
internal static class ProviderFactsShape
{
    internal static bool SafeReason(string? reason) => reason is null or "not_installed" or "cannot_start" or
        "invalid_descriptor" or "timed_out" or "adapter_crashed" or "network" or "version_unknown" or "interrupted" or "adapter_error";

    internal static void Validate(RecordProviderCheck command)
    {
        if (command.Probe is null || !SafeReason(command.Reason) ||
            command.Installation is not ("installed" or "not_installed" or "unknown") ||
            command.AuthState is not ("signed_in" or "unverified" or "needs_sign_in" or "unsupported" or "could_not_check")) Invalid();
        var facts = command.Facts;
        if (facts.ValueKind == JsonValueKind.Null) return;
        Object(facts, ["provider", "name", "adapter", "version", "installation", "auth", "problem", "models", "methods", "checkedMs"]);
        foreach (var key in new[] { "provider", "name", "adapter" }) String(facts.GetProperty(key));
        NullableString(facts.GetProperty("version"));
        var installation = String(facts.GetProperty("installation"));
        var mappedInstallation = installation switch { "installed" => "installed", "notInstalled" => "not_installed", "invalid" => "unknown", _ => null };
        var auth = String(facts.GetProperty("auth"));
        var mappedAuth = auth switch { "signedIn" => "signed_in", "needsSignIn" => "needs_sign_in", "unverified" => "unverified", "unsupported" => "unsupported", "unknown" => "could_not_check", _ => null };
        if (mappedInstallation != command.Installation || mappedAuth != command.AuthState ||
            !SafeReason(NullableString(facts.GetProperty("problem"))) || !facts.GetProperty("checkedMs").TryGetInt64(out _)) Invalid();
        var models = facts.GetProperty("models"); var methods = facts.GetProperty("methods");
        if (models.ValueKind != JsonValueKind.Array || methods.ValueKind != JsonValueKind.Array) Invalid();
        foreach (var model in models.EnumerateArray())
        {
            Object(model, ["id", "name"]); String(model.GetProperty("id")); String(model.GetProperty("name"));
        }
        foreach (var method in methods.EnumerateArray())
        {
            Object(method, ["methodId", "name", "kind"]); String(method.GetProperty("methodId")); String(method.GetProperty("name"));
            if (String(method.GetProperty("kind")) is not ("agent" or "terminal")) Invalid();
        }
    }

    private static void Object(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) Invalid();
        if (seen.Count != fields.Length) Invalid();
    }
    private static string String(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) Invalid();
        return value.GetString()!;
    }
    private static string? NullableString(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : String(value);
    private static void Invalid() => throw new ArgumentException("Invalid safe provider facts.");
}
