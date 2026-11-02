using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Common.Agents;

public static class AccountContext
{
    private const string Prefix = "acct-v1:";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Of(AgentDescriptor agent, Func<string, string?> inheritedEnvironment)
    {
        var variable = agent.VendorFamily switch { "codex" => "CODEX_HOME", "claude" => "CLAUDE_CONFIG_DIR", _ => null };
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
