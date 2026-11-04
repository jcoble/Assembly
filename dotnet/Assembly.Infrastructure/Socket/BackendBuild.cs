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

    /// <summary>The inputs on the list of changes that need the remote to change too (scripts/dev-remote-identity.ts).</summary>
    internal static string? RemoteContract { get; } = Metadata("RemoteContract");

    /// <summary>The real release version, which moves only on a release and sign.</summary>
    internal static string? Version { get; } = Metadata("ReleaseVersion");

    private static string? Metadata(string key) => typeof(BackendBuild).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>What a machine's welcome says it runs, against this build. Information only: nothing is refused on it.</summary>
    internal static RemoteBackend Compare(string? identity, string? commit, string? version) =>
        new(commit, Commit, identity != Identity, version, Version);

    internal static string? ReadCommit(string? informationalVersion) =>
        informationalVersion?.Split('+') is [_, { Length: > 0 } commit] ? commit : null;
}
