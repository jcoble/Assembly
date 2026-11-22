using System.Globalization;
using System.Text.Json;

namespace Assembly.Application.Common.Agents;

/// <summary>
/// Provider-neutral effective context window policy: an optional fixed auto-compact limit, plus whether later
/// turn-end model capacity reports must never widen an already-observed window.
/// </summary>
public sealed record ContextLimitPolicy(long? FixedLimit, bool NeverWiden);

/// <summary>
/// Resolves each provider's user-level effective auto-compact limit from its own config home.
/// </summary>
public static class EffectiveContextLimit
{
    public static ContextLimitPolicy? Resolve(
        AgentDescriptor descriptor,
        IReadOnlyDictionary<string, string> environment,
        Func<string, string?> readFile)
    {
        var kind = AccountContext.ContextLimitKind(descriptor.VendorFamily);
        if (kind == ContextLimitSource.None) return null;
        var configHome = AccountContext.ConfigHome(descriptor.VendorFamily, environment);
        return kind switch
        {
            ContextLimitSource.JsonAutoCompactWindow => ResolveJsonAutoCompactWindow(configHome, environment, readFile),
            ContextLimitSource.TomlAutoCompactLimit => ResolveTomlAutoCompactLimit(configHome, readFile),
            _ => null,
        };
    }

    private static ContextLimitPolicy ResolveJsonAutoCompactWindow(
        string? configHome,
        IReadOnlyDictionary<string, string> environment,
        Func<string, string?> readFile)
    {
        var autoCompactEnabled = true;
        long? autoCompactWindow = null;
        string? disableAutoCompact = null;

        if (configHome is not null && readFile(Path.Combine(configHome, "settings.json")) is { Length: > 0 } text)
        {
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = document.RootElement;
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

        if (!autoCompactEnabled || IsTruthy(disableAutoCompact))
            return new ContextLimitPolicy(null, NeverWiden: false);

        return new ContextLimitPolicy(autoCompactWindow, NeverWiden: true);
    }

    private static ContextLimitPolicy? ResolveTomlAutoCompactLimit(string? configHome, Func<string, string?> readFile)
    {
        if (configHome is null || readFile(Path.Combine(configHome, "config.toml")) is not { Length: > 0 } text)
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

    private static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
