namespace Assembly.Desktop.Settings;

/// <summary>Data for one provider Assembly manages: identity, display name, CLI vendor family and stock adapter package.</summary>
internal sealed record ProviderSpec(string Id, string Name, string? VendorFamily, string? AdapterPackage);

/// <summary>Single data table of providers Assembly knows about; provider differences stay data here, never branches.</summary>
internal static class ProviderCatalog
{
    internal static readonly IReadOnlyList<ProviderSpec> Known =
    [
        new("claude", "Claude Code", "claude", "claude-agent-acp"),
        new("codex", "Codex", "codex", "codex-acp"),
    ];
}
