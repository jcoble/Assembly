using System.Text.Json;
using System.Text.Json.Serialization;
using Assembly.Application.Common.Acp.Messages;

namespace Assembly.Application.Features.Agents;

public enum SignInKind
{
    [JsonStringEnumMemberName("agent")] Agent,
    [JsonStringEnumMemberName("terminal")] Terminal
}
public sealed record RunnableMethod(string MethodId, string Name, SignInKind Kind,
    IReadOnlyList<string> Args, IReadOnlyList<AuthEnv> Env);

public static class SignInMethods
{
    public static IReadOnlyList<RunnableMethod> Runnable(IReadOnlyList<AuthMethod> advertised) => advertised
        .Where(method => method.Type is "agent" or "terminal" && !Excluded(method.MethodId) && !ExcludedMeta(method.Meta))
        .Select(method => new RunnableMethod(method.MethodId, method.Name,
            method.Type == "agent" ? SignInKind.Agent : SignInKind.Terminal,
            method.Args?.ToArray() ?? [], method.Env?.ToArray() ?? [])).ToArray();

    public static RunnableMethod? Choose(IReadOnlyList<RunnableMethod> runnable) =>
        runnable.FirstOrDefault(method => !method.MethodId.Contains("device", StringComparison.OrdinalIgnoreCase))
        ?? runnable.FirstOrDefault();

    public static SignInChoice Present(RunnableMethod method) =>
        new(method.MethodId, method.Name, method.Kind == SignInKind.Agent ? "agent" : "terminal");

    public static (string Host, int Port)? Callback(Uri link)
    {
        if (!link.IsAbsoluteUri) return null;
        foreach (var pair in link.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) != "redirect_uri" || parts.Length != 2) continue;
            if (!Uri.TryCreate(Uri.UnescapeDataString(parts[1]), UriKind.Absolute, out var redirect)
                || redirect.Scheme is not ("http" or "https") || !redirect.IsLoopback
                || !string.IsNullOrEmpty(redirect.UserInfo)) return null;
            return (redirect.Host, redirect.Port);
        }
        return null;
    }

    private static bool Excluded(string value) => value.Contains("gateway", StringComparison.OrdinalIgnoreCase)
        || value.Replace('_', '-').Contains("api-key", StringComparison.OrdinalIgnoreCase);

    private static bool ExcludedMeta(JsonElement? meta)
    {
        if (meta is { ValueKind: JsonValueKind.Array } array)
            return array.EnumerateArray().Any(value => ExcludedMeta(value));
        if (meta is not { ValueKind: JsonValueKind.Object } value) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name == "gateway" && property.Value.ValueKind == JsonValueKind.True) return true;
            if (property.Name is "mode" or "authType" && property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is "key" or "api-key" or "api_key" or "gateway") return true;
            if (ExcludedMeta(property.Value)) return true;
        }
        return false;
    }
}
