using System.Reflection;
using Assembly.Application.Common.Machines;

namespace Assembly.Infrastructure.Socket;

/// <summary>The build identity stamped by the SDK, shared by the existing HTTP and socket readers.</summary>
internal static class BackendBuild
{
    internal static string? Commit { get; } = ReadCommit(typeof(BackendBuild).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Every compile input: an exact match, what a check before a PR requires.</summary>
    internal static string? Identity { get; } = Metadata("BackendIdentity");

    /// <summary>The one version number: <c>version</c> under [package] in desktop/Cargo.toml. A remote on another one gets replaced, and a release tag must equal it.</summary>
    internal static string? Version { get; } = Metadata("ReleaseVersion");

    /// <summary>The adapters, launcher and their scripts this build was made with (scripts/dev-remote-identity.ts).</summary>
    internal static string? AdapterSources { get; } = Metadata("AdapterSources");

    private static string? Metadata(string key) => typeof(BackendBuild).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>What a machine's welcome says it runs, against this build. Nothing is refused on it.</summary>
    internal static RemoteBackend Compare(string? identity, string? commit, string? version) =>
        new(commit, Commit, identity != Identity, version, Version);

    internal static string? ReadCommit(string? informationalVersion) =>
        informationalVersion?.Split('+') is [_, { Length: > 0 } commit] ? commit : null;
}
