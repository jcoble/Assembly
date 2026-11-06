namespace Assembly.Application.Features.Agents;

/// <summary>Selected provider settings for its owning machine; not a public provider-discovery record.</summary>
public sealed record ProviderRuntimeSettings(string? BinaryPath, string? ConfigHome, string? ShadowHome,
    IReadOnlyList<string> LaunchArguments);

public sealed record ProviderRuntimeReadArgs(string Machine, string Provider);

public sealed record ProviderRuntimeSaveArgs(string Machine, string Provider, string ExpectedRevision,
    ProviderRuntimeSettings Settings);

public sealed record ProviderRuntimeReply(string Machine, string Provider, string Revision,
    string ConfigurationTargetId, string AccountContext, ProviderRuntimeSettings Settings);

public sealed record ProviderRuntimeChanged(string Machine, string Revision);
