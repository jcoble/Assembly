using System.Text.Json.Serialization;

namespace Assembly.Infrastructure.Remote.Install;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteInstallInspect(string BundleFile);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteInstallStart(Guid OperationId, string Machine, string ExpectedEngine, string BundleFile, string Sha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteInstallOperation(Guid OperationId);
internal sealed record RemoteInstallBundle(string Sha256, long Bytes, string Commit, string Target, int Protocol, int Format, string BuildKind);
internal sealed record RemoteInstallResult(string Commit, string Machine, string Engine);
internal sealed record RemoteInstallJob(Guid OperationId, long Version, string Machine, string State, string Phase,
    long BytesDone, long TotalBytes, string? ErrorCode, RemoteInstallResult? Result);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteInstallFramework(string Name, string Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteInstallFile(string Path, long Bytes, string Sha256, string Mode);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteInstallManifest(int SchemaVersion, string PackageType, string BuildKind, string Target,
    string Commit, int Protocol, int Format, RemoteInstallFramework[] Frameworks, RemoteInstallFile[] Files);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteInstallInspect))]
[JsonSerializable(typeof(RemoteInstallStart))]
[JsonSerializable(typeof(RemoteInstallOperation))]
[JsonSerializable(typeof(RemoteInstallBundle))]
[JsonSerializable(typeof(RemoteInstallJob))]
[JsonSerializable(typeof(RemoteInstallManifest))]
internal sealed partial class RemoteInstallJson : JsonSerializerContext;
