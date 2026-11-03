using System.Security.Cryptography;
using System.Text.Unicode;
using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Revision validation and same-directory atomic replacement; a filesystem commit is not a database rollback.</summary>
public sealed class FileSave
{
    private readonly Func<Task>? beforeReplace;
    internal FileSave(Func<Task>? beforeReplace = null) => this.beforeReplace = beforeReplace;

    public static async Task<string> RevisionAsync(string fullPath, CancellationToken ct)
    {
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    public static Task<SaveResult> ReplaceAsync(string root, string path, string expectedRevision,
        ReadOnlyMemory<byte> bytes, CancellationToken ct) => new FileSave().ReplaceCoreAsync(root, path, expectedRevision, bytes, ct);

    internal async Task<SaveResult> ReplaceCoreAsync(string root, string path, string expectedRevision,
        ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var invalid = Validate(bytes.Span);
        if (invalid is not null) return invalid;
        var full = SessionPaths.ResolveFile(root, path);
        var stage = Path.Combine(Path.GetDirectoryName(full)!, ".assembly-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            return await CommitValidatedAsync(root, path, expectedRevision, stage,
                Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), ct);
        }
        catch (DirectoryNotFoundException) { return Conflict("The target folder is missing."); }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    internal static async Task<SaveResult> CommitStageAsync(string root, string path, string expectedRevision,
        string stage, CancellationToken ct, Action? validateOwner = null)
    {
        var info = new FileInfo(stage);
        if (info.Length > FileRead.MaxBytes) return Failed("too_large", "This draft is larger than 8 MiB.");
        var bytes = await File.ReadAllBytesAsync(stage, ct);
        var invalid = Validate(bytes);
        if (invalid is not null) return invalid;
        return await new FileSave().CommitValidatedAsync(root, path, expectedRevision, stage,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), ct, validateOwner);
    }

    private async Task<SaveResult> CommitValidatedAsync(string root, string path, string expectedRevision,
        string stage, string revision, CancellationToken ct, Action? validateOwner = null)
    {
        validateOwner?.Invoke();
        var full = SessionPaths.ResolveFile(root, path);
        using var order = await FileMutationOrder.EnterAsync(full, ct);
        if (!File.Exists(full) || Directory.Exists(full)) return Conflict("The target file is missing.");
        if (await RevisionAsync(full, ct) != expectedRevision) return Conflict("The file changed on disk.");
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, File.GetUnixFileMode(full));
            if (beforeReplace is not null) await beforeReplace();
            ct.ThrowIfCancellationRequested();
            if (SessionPaths.ResolveFile(root, path) != full || !File.Exists(full)) return Conflict("The target file is missing.");
            if (await RevisionAsync(full, ct) != expectedRevision) return Conflict("The file changed on disk.");
            ct.ThrowIfCancellationRequested();
            validateOwner?.Invoke();
            File.Move(stage, full, overwrite: true);
            // Cancellation after the rename cannot roll back or hide the applied outcome.
            return new SaveResult("saved", revision, null);
        }
        catch (FileNotFoundException) { return Conflict("The target file is missing."); }
        catch (DirectoryNotFoundException) { return Conflict("The target folder is missing."); }
    }

    private static SaveResult? Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > FileRead.MaxBytes) return Failed("too_large", "This draft is larger than 8 MiB.");
        if (bytes.Contains((byte)0) || !Utf8.IsValid(bytes)) return Failed("invalid_text", "The draft must be UTF-8 text without NUL bytes.");
        return null;
    }

    private static SaveResult Conflict(string message) => new("conflict", null, new FileFault("revision_changed", message));
    private static SaveResult Failed(string code, string message) => new("failed", null, new FileFault(code, message));
}
