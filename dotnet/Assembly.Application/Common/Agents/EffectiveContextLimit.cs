using System.Globalization;
using System.Text.Json;

namespace Assembly.Application.Common.Agents;

/// <summary>
/// Effective context window policy resolved at the adapter boundary from the provider's user/project configuration.
/// <c>ConfiguredLimit</c> caps the reported context window to the user's configured auto-compact limit;
/// <c>ClampWidening</c> prevents a later turn-end model capacity report from widening an already-observed window.
/// </summary>
public sealed record ContextLimitPolicy(long? ConfiguredLimit, bool ClampWidening);

/// <summary>
/// Resolves each provider's effective context limit from its own configuration files so the backend pushes the
/// effective limit and the UI stays provider-neutral. Reads files through a caller-supplied delegate so the core
/// stays free of direct filesystem I/O.
/// </summary>
public static class EffectiveContextLimit
{
    public static ContextLimitPolicy? Resolve(
        AgentDescriptor descriptor,
        IReadOnlyDictionary<string, string> environment,
        string? folder,
        Func<string, string?> readFile)
    {
        var kind = AccountContext.ContextLimitKind(descriptor.VendorFamily);
        if (kind == ContextLimitSource.None) return null;
        var configHome = AccountContext.ConfigHome(descriptor.VendorFamily, environment);
        var projectFolder = AccountContext.DefaultHomeFolder(descriptor.VendorFamily);
        return kind switch
        {
            ContextLimitSource.JsonAutoCompactWindow => ResolveJsonAutoCompactWindow(configHome, projectFolder, folder, environment, readFile),
            ContextLimitSource.TomlAutoCompactLimit => ResolveTomlAutoCompactLimit(configHome, projectFolder, folder, environment, readFile),
            _ => null,
        };
    }

    private static ContextLimitPolicy ResolveJsonAutoCompactWindow(
        string? configHome,
        string projectFolder,
        string? folder,
        IReadOnlyDictionary<string, string> environment,
        Func<string, string?> readFile)
    {
        List<string> paths = [];
        if (configHome is not null)
            paths.Add(Path.Combine(configHome, "settings.json"));
        if (!string.IsNullOrEmpty(folder) && Path.IsPathFullyQualified(folder))
        {
            paths.Add(Path.Combine(folder, projectFolder, "settings.json"));
            paths.Add(Path.Combine(folder, projectFolder, "settings.local.json"));
        }
        if (ManagedJsonSettingsPath() is { } managed)
            paths.Add(managed);

        var autoCompactEnabled = true;
        long? autoCompactWindow = null;
        string? disableAutoCompact = null;

        foreach (var path in paths)
        {
            var text = readFile(path);
            if (string.IsNullOrWhiteSpace(text)) continue;
            ApplyJsonSettings(text, ref autoCompactEnabled, ref autoCompactWindow, ref disableAutoCompact);
        }

        if (environment.TryGetValue("DISABLE_AUTO_COMPACT", out var envDisable) && !string.IsNullOrEmpty(envDisable))
            disableAutoCompact = envDisable;

        if (!autoCompactEnabled || IsTruthy(disableAutoCompact))
            return new ContextLimitPolicy(null, ClampWidening: false);

        return new ContextLimitPolicy(autoCompactWindow is > 0 ? autoCompactWindow : null, ClampWidening: true);
    }

    private static ContextLimitPolicy? ResolveTomlAutoCompactLimit(
        string? configHome,
        string projectFolder,
        string? folder,
        IReadOnlyDictionary<string, string> environment,
        Func<string, string?> readFile)
    {
        long? topLevelLimit = null;
        string? activeProfile = null;
        Dictionary<string, long> profileLimits = new(StringComparer.Ordinal);

        if (configHome is not null && readFile(Path.Combine(configHome, "config.toml")) is { Length: > 0 } userToml)
            ApplyTomlConfig(userToml, ref topLevelLimit, ref activeProfile, profileLimits);

        if (!string.IsNullOrEmpty(folder) && Path.IsPathFullyQualified(folder)
            && readFile(Path.Combine(folder, projectFolder, "config.toml")) is { Length: > 0 } projectToml)
            ApplyTomlConfig(projectToml, ref topLevelLimit, ref activeProfile, profileLimits);

        if (environment.TryGetValue("CODEX_CONFIG", out var envJson) && !string.IsNullOrWhiteSpace(envJson))
            ApplyJsonTomlOverride(envJson, ref topLevelLimit, ref activeProfile, profileLimits);

        var limit = activeProfile is { Length: > 0 } && profileLimits.TryGetValue(activeProfile, out var profileLimit)
            ? profileLimit
            : topLevelLimit;

        return limit is > 0 ? new ContextLimitPolicy(limit, ClampWidening: false) : null;
    }

    private static void ApplyJsonSettings(string text, ref bool autoCompactEnabled, ref long? autoCompactWindow, ref string? disableAutoCompact)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            var root = document.RootElement;

            if (root.TryGetProperty("autoCompactEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                autoCompactEnabled = enabled.GetBoolean();

            if (root.TryGetProperty("autoCompactWindow", out var window))
            {
                if (window.ValueKind == JsonValueKind.Null)
                    autoCompactWindow = null;
                else if (window.ValueKind == JsonValueKind.Number && window.TryGetInt64(out var value) && value > 0)
                    autoCompactWindow = value;
            }

            if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
                && env.TryGetProperty("DISABLE_AUTO_COMPACT", out var disable) && disable.ValueKind == JsonValueKind.String)
                disableAutoCompact = disable.GetString();
        }
        catch (JsonException) { }
    }

    private static void ApplyJsonTomlOverride(string text, ref long? topLevelLimit, ref string? activeProfile, Dictionary<string, long> profileLimits)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            var root = document.RootElement;

            if (root.TryGetProperty("model_auto_compact_token_limit", out var limit)
                && limit.ValueKind == JsonValueKind.Number && limit.TryGetInt64(out var value) && value > 0)
                topLevelLimit = value;

            if (root.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.String)
                activeProfile = profile.GetString();

            if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in profiles.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Object
                        && entry.Value.TryGetProperty("model_auto_compact_token_limit", out var entryLimit)
                        && entryLimit.ValueKind == JsonValueKind.Number && entryLimit.TryGetInt64(out var parsed) && parsed > 0)
                        profileLimits[entry.Name] = parsed;
                }
            }
        }
        catch (JsonException) { }
    }

    private static void ApplyTomlConfig(string text, ref long? topLevelLimit, ref string? activeProfile, Dictionary<string, long> profileLimits)
    {
        var inTopLevel = true;
        string? currentProfile = null;
        string? multilineDelimiter = null;

        foreach (var rawLine in text.Split('\n'))
        {
            if (multilineDelimiter is not null)
            {
                if (rawLine.Contains(multilineDelimiter, StringComparison.Ordinal))
                    multilineDelimiter = null;
                continue;
            }

            var line = StripTomlComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line[0] == '[')
            {
                inTopLevel = false;
                currentProfile = ParseProfileTableHeader(line);
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;

            var value = line[(equals + 1)..].Trim();
            if (StartsUnclosedMultiline(value, out var delimiter))
            {
                multilineDelimiter = delimiter;
                continue;
            }

            var key = line[..equals].Trim();
            if (inTopLevel)
            {
                if (key == "model_auto_compact_token_limit" && TryParseTomlPositiveInt64(value, out var limit))
                    topLevelLimit = limit;
                else if (key == "profile" && TryParseTomlString(value, out var profile))
                    activeProfile = profile;
            }
            else if (currentProfile is not null && key == "model_auto_compact_token_limit" && TryParseTomlPositiveInt64(value, out var profileLimit))
            {
                profileLimits[currentProfile] = profileLimit;
            }
        }
    }

    private static string? ParseProfileTableHeader(string line)
    {
        if (line.StartsWith("[[", StringComparison.Ordinal) || !line.EndsWith(']')) return null;
        var inner = line[1..^1].Trim();
        var segments = SplitTomlDottedKey(inner);
        return segments is ["profiles", { Length: > 0 } name] ? name : null;
    }

    private static List<string>? SplitTomlDottedKey(string input)
    {
        List<string> segments = [];
        var i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            if (i >= input.Length) return null;

            string segment;
            if (input[i] is '"' or '\'')
            {
                var quote = input[i++];
                var start = i;
                while (i < input.Length && input[i] != quote)
                {
                    if (quote == '"' && input[i] == '\\' && i + 1 < input.Length) i += 2;
                    else i++;
                }
                if (i >= input.Length) return null;
                segment = input[start..i];
                i++;
            }
            else
            {
                var start = i;
                while (i < input.Length && input[i] != '.' && !char.IsWhiteSpace(input[i])) i++;
                segment = input[start..i];
            }

            segments.Add(segment);
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            if (i == input.Length) break;
            if (input[i] != '.') return null;
            i++;
        }
        return segments;
    }

    private static string StripTomlComment(string line)
    {
        var inDouble = false;
        var inSingle = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && inDouble && i + 1 < line.Length) { i++; continue; }
            if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '#' && !inDouble && !inSingle) return line[..i];
        }
        return line;
    }

    private static bool StartsUnclosedMultiline(string value, out string delimiter)
    {
        foreach (var candidate in new[] { "\"\"\"", "'''" })
        {
            if (value.StartsWith(candidate, StringComparison.Ordinal)
                && value.IndexOf(candidate, 3, StringComparison.Ordinal) < 0)
            {
                delimiter = candidate;
                return true;
            }
        }
        delimiter = "";
        return false;
    }

    private static bool TryParseTomlPositiveInt64(string value, out long parsed)
    {
        var clean = value.Replace("_", "", StringComparison.Ordinal);
        return long.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
    }

    private static bool TryParseTomlString(string value, out string parsed)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind == JsonValueKind.String)
                {
                    parsed = document.RootElement.GetString() ?? "";
                    return true;
                }
            }
            catch (JsonException) { }
        }
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            parsed = value[1..^1];
            return true;
        }
        parsed = "";
        return false;
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string? ManagedJsonSettingsPath() =>
        OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode/managed-settings.json"
        : OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode\managed-settings.json"
        : OperatingSystem.IsLinux() ? "/etc/claude-code/managed-settings.json"
        : null;
}
