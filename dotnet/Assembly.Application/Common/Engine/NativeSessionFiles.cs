namespace Assembly.Application.Common.Engine;

public sealed record ProviderFileContext(string MachineId, string ProviderId,
    string VendorFamily, string AccountContext, string LaunchBindingId,
    string ProviderHome, string? CodexSqliteHome);
public sealed record NativeSessionWitness(string SessionId, long Epoch,
    string MachineId, string ProviderId, string AccountContext, string LaunchBindingId,
    string AcpSessionId, string ResumeId, string Folder, string SelectedRelativePath,
    string? RolloutId, string HistoryMode, string Compression);
public sealed record NativeSessionCapture(ProviderFileContext Storage, NativeSessionWitness Current);
public interface INativeSessionWitnessSource
{
    Task<NativeSessionCapture> ReadCurrentAsync(string sessionId, long expectedEpoch,
        CancellationToken cancellationToken);
}
public sealed record NativeFile(string RelativePath, string NativeId, string? RolloutId);
public interface IProviderSessionFile
{
    string Provider { get; }
    NativeFile? Locate(ProviderFileContext storage, NativeSessionWitness current);
    string TargetPath(ProviderFileContext target, string newFolder,
        NativeSessionCapture source, NativeFile file);
    string MapLine(string jsonLine, string oldFolder, string newFolder);
}
public sealed record SessionFileStat(string? SourceSha256, string? MappedSha256, long Bytes,
    NativeFile? File, NativeSessionCapture Source);
public sealed record SessionFileReadArgs(string HandoffId, NativeSessionCapture Source, long Offset);
public sealed record SessionFileChunk(string HandoffId, string Provider,
    NativeSessionCapture Source, string Folder, NativeFile File, long Bytes,
    string SourceSha256, string MappedSha256, long Offset, string Base64, bool Last);
