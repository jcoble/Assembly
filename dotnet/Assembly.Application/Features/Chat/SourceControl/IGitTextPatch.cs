namespace Assembly.Application.Features.Chat.SourceControl;

/// <summary>Compares supplied file versions without granting a caller checkout write authority.</summary>
public interface IGitTextPatch
{
    Task<GitFileDiff> ReadAsync(string path, string? originalPath, ReadOnlyMemory<byte>? original,
        ReadOnlyMemory<byte>? modified, CancellationToken cancellationToken);
    void ValidatePath(string path);
}
