using System.Reflection;

namespace Assembly.Infrastructure.Socket;

/// <summary>The build identity stamped by the SDK, shared by the existing HTTP and socket readers.</summary>
internal static class BackendBuild
{
    internal static string? Commit { get; } = ReadCommit(typeof(BackendBuild).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string? Identity { get; } = typeof(BackendBuild).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BackendIdentity")?.Value;

    internal static string? Mismatch(string? identity, string? commit) =>
        Identity is { Length: 64 } && identity == Identity ? null :
        $"Remote is on an older or different backend (remote {commit ?? "unknown"}; desktop {Commit ?? "unknown"}). New remote sessions are blocked until the backends match.";

    internal static string? ReadCommit(string? informationalVersion) =>
        informationalVersion?.Split('+') is [_, { Length: > 0 } commit] ? commit : null;
}
