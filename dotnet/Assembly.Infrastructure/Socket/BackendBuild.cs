using System.Reflection;

namespace Assembly.Infrastructure.Socket;

/// <summary>The build identity stamped by the SDK, shared by the existing HTTP and socket readers.</summary>
internal static class BackendBuild
{
    internal static string? Commit { get; } = ReadCommit(typeof(BackendBuild).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string? ReadCommit(string? informationalVersion) =>
        informationalVersion?.Split('+') is [_, { Length: > 0 } commit] ? commit : null;
}
