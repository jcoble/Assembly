using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Common.Agents;

/// <summary>Which provider user-level configuration format supplies an effective auto-compact context limit.</summary>
public enum ContextLimitSource
{
    None,
    /// <summary>User <c>settings.json</c> with <c>autoCompactWindow</c> and non-widening effective window.</summary>
    JsonAutoCompactWindow,
    /// <summary>User <c>config.toml</c> top-level <c>model_auto_compact_token_limit</c>.</summary>
    TomlAutoCompactLimit,
}

public static class AccountContext
{
    private const string Prefix = "acct-v1:";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string? ConfigHomeVariable(string? vendorFamily) => vendorFamily switch
    {
        "codex" => "CODEX_HOME",
        "claude" => "CLAUDE_CONFIG_DIR",
        _ => null
    };

    /// <summary>Settings files that a read-only composer probe does not rewrite.</summary>
    public static (string Profile, string[] Project) CatalogueFiles(string vendorFamily) => vendorFamily switch
    {
        "claude" => ("settings.json", [".claude/settings.json", ".claude/settings.local.json"]),
        "codex" => ("config.toml", [".codex/config.toml"]),
        _ => ("", [])
    };

    public static ContextLimitSource ContextLimitKind(string? vendorFamily) => vendorFamily switch
    {
        "claude" => ContextLimitSource.JsonAutoCompactWindow,
        "codex" => ContextLimitSource.TomlAutoCompactLimit,
        _ => ContextLimitSource.None,
    };

    public static string? ConfigHome(string? vendorFamily, IReadOnlyDictionary<string, string> environment)
    {
        var variable = ConfigHomeVariable(vendorFamily);
        if (variable is null) return null;
        var home = environment.GetValueOrDefault(variable);
        if (string.IsNullOrEmpty(home))
        {
            var user = environment.GetValueOrDefault(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
            if (string.IsNullOrEmpty(user) || !Path.IsPathFullyQualified(user)) return null;
            home = Path.Combine(user, DefaultHomeFolder(vendorFamily));
        }
        return Path.IsPathFullyQualified(home) ? Path.GetFullPath(home) : null;
    }

    public static string? ExecutableVariable(string? vendorFamily) => vendorFamily switch
    {
        "codex" => "CODEX_PATH",
        "claude" => "CLAUDE_CODE_EXECUTABLE",
        _ => null
    };

    /// <summary>The folder under the user's home each provider uses when no config home is set.</summary>
    public static string DefaultHomeFolder(string? vendorFamily) => "." + vendorFamily;

    public static string Of(AgentDescriptor agent, Func<string, string?> inheritedEnvironment)
    {
        var variable = ConfigHomeVariable(agent.VendorFamily);
        if (variable is null) return "";
        var raw = agent.Env?.TryGetValue(variable, out var explicitValue) == true
            ? explicitValue : inheritedEnvironment(variable) ?? "";
        if (string.IsNullOrEmpty(raw)) return "";
        byte[] bytes;
        try { bytes = Utf8.GetBytes(raw); }
        catch (EncoderFallbackException) { throw new CallFailed("invalid account context", "invalid_account_context"); }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Assembly.AccountContext/v1\0"u8);
        hash.AppendData(bytes);
        return Prefix + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Checks identity syntax only; persisted identity provenance remains owned by Engine initialization.</summary>
    public static bool IsValid(string? value)
    {
        if (value is null) return false;
        if (value.Length == 0) return true;
        if (value.Length != Prefix.Length + 64 || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        foreach (var character in value.AsSpan(Prefix.Length))
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
