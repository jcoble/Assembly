namespace Assembly.Application.Common.Engine;

/// <summary>Non-secret facts frozen from the environment used for an accepted adapter open.</summary>
public sealed record NativeSessionLaunch(string ProviderId, string VendorFamily, string AccountContext,
    string LaunchBindingId, string ProviderHome, string AcpSessionId, string Folder);

public interface INativeSessionResolver
{
    /// <summary><paramref name="movingTo"/> names a folder an interrupted move may already have mapped the file to.</summary>
    NativeSessionCapture Resolve(NativeSessionLaunch launch, string session, string? movingTo = null);
    /// <summary>Makes the transcript readable from the folder. True when <see cref="UndoFolderAsync"/> must reverse it if resume fails.</summary>
    Task<bool> PrepareFolderAsync(NativeSessionCapture capture, string folder, CancellationToken ct);
    Task UndoFolderAsync(NativeSessionCapture capture, string folder, CancellationToken ct);
}

/// <summary>Actual resume response plus positive disposal of its exact adapter borrower.</summary>
public sealed record NativeResumeEvidence(string OperationId, NativeSessionCapture Capture, string Folder);

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
    /// <summary>Where this provider keeps the launch's conversation, from its own layout.</summary>
    NativeFile Select(NativeSessionLaunch launch);
    string? SqliteHome(string providerHome);
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
