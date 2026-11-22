using System.Globalization;
using System.Text.Json;
using Assembly.Application.Common.Agents;

namespace Assembly.Infrastructure.Agents;

/// <summary>
/// Resolves a provider-neutral <see cref="ContextLimitPolicy"/> at the adapter boundary from the provider's
/// user-level configuration on this host.
/// </summary>
internal static class ContextLimitResolver
{
    public static ContextLimitPolicy? Resolve(AgentDescriptor descriptor, IReadOnlyDictionary<string, string> environment)
    {
        var kind = AccountContext.ContextLimitKind(descriptor.VendorFamily);
        if (kind == ContextLimitSource.None) return null;
        var configHome = AccountContext.ConfigHome(descriptor.VendorFamily, environment);
        return kind switch
        {
            ContextLimitSource.JsonAutoCompactWindow => ResolveJsonAutoCompactWindow(configHome, environment),
            ContextLimitSource.TomlAutoCompactLimit => ResolveTomlAutoCompactLimit(configHome),
            _ => null,
        };
    }

    private static ContextLimitPolicy ResolveJsonAutoCompactWindow(string? configHome, IReadOnlyDictionary<string, string> environment)
    {
        var autoCompactEnabled = true;
        long? autoCompactWindow = null;
        string? disableAutoCompact = null;

        if (configHome is not null && ReadText(Path.Combine(configHome, "settings.json")) is { Length: > 0 } text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("autoCompactEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        autoCompactEnabled = enabled.GetBoolean();
                    if (root.TryGetProperty("autoCompactWindow", out var window) && window.ValueKind == JsonValueKind.Number
                        && window.TryGetInt64(out var value) && value > 0)
                        autoCompactWindow = value;
                    if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
                        && env.TryGetProperty("DISABLE_AUTO_COMPACT", out var disable) && disable.ValueKind == JsonValueKind.String)
                        disableAutoCompact = disable.GetString();
                }
            }
            catch (JsonException) { }
        }

        if (environment.TryGetValue("DISABLE_AUTO_COMPACT", out var envDisable) && !string.IsNullOrEmpty(envDisable))
            disableAutoCompact = envDisable;

        var enabledEffective = autoCompactEnabled
            && disableAutoCompact is not ("1") && !string.Equals(disableAutoCompact, "true", StringComparison.OrdinalIgnoreCase);
        return enabledEffective ? new ContextLimitPolicy(autoCompactWindow, NeverWiden: true) : new ContextLimitPolicy(null, NeverWiden: false);
    }

    private static ContextLimitPolicy? ResolveTomlAutoCompactLimit(string? configHome)
    {
        if (configHome is null || ReadText(Path.Combine(configHome, "config.toml")) is not { Length: > 0 } text)
            return null;

        foreach (var rawLine in text.Split('\n'))
        {
            var comment = rawLine.IndexOf('#');
            var line = (comment >= 0 ? rawLine[..comment] : rawLine).Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[') break;
            var equals = line.IndexOf('=');
            if (equals <= 0 || !line[..equals].Trim().Equals("model_auto_compact_token_limit", StringComparison.Ordinal))
                continue;
            var clean = line[(equals + 1)..].Trim().Replace("_", "", StringComparison.Ordinal);
            if (long.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0)
                return new ContextLimitPolicy(limit, NeverWiden: false);
        }
        return null;
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
