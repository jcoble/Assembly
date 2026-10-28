using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>
/// Reads a file in a session's folder for the read-only view, a part at a time. A part is at most <see cref="ChunkBytes"/>
/// and ends at a line end when one is in reach, else between two characters. A file holding a NUL byte or bytes that
/// aren't UTF-8 is binary. Files past <see cref="MaxBytes"/> are refused.
/// </summary>
public static class FileRead
{
    /// <summary>The view's size limit.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// A part's most bytes: JSON writes a control character as six bytes, so even such text stays well inside the
    /// socket's 4 MiB frame.
    /// </summary>
    public const int ChunkBytes = 512 * 1024;

    public static async Task<FileChunk> ReadAsync(string root, string path, long offset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var full = SessionPaths.Resolve(root, path);
        if (Directory.Exists(full))
            throw new CallFailed("that isn't a file");
        var file = new FileInfo(full);
        if (!file.Exists)
            throw new CallFailed("that file isn't there");
        if (file.Length > MaxBytes)
            throw new CallFailed($"that file is {file.Length / (1024.0 * 1024):0.#} MiB, more than the {MaxBytes / (1024 * 1024)} MiB this view shows");
        var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
        if (offset < 0 || offset > bytes.Length)
            throw new CallFailed("the offset is outside the file");
        var revision = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.AsSpan().Contains((byte)0) || !Utf8.IsValid(bytes))
            return new FileChunk("", bytes.Length, revision, 0, true, null);
        var start = (int)offset;
        var end = Math.Min(start + ChunkBytes, bytes.Length);
        if (end < bytes.Length)
        {
            var lineEnd = bytes.AsSpan(start, end - start).LastIndexOf((byte)'\n');
            if (lineEnd >= 0)
                end = start + lineEnd + 1;
            else
                // Back off a UTF-8 character's continuation bytes, so the part ends between characters.
                while (end > start && (bytes[end] & 0xC0) == 0x80)
                    end--;
        }
        var lines = bytes.AsSpan().Count((byte)'\n') + (bytes.Length > 0 && bytes[^1] != '\n' ? 1 : 0);
        return new FileChunk(Encoding.UTF8.GetString(bytes, start, end - start), bytes.Length, revision, lines, false,
            end < bytes.Length ? end : null);
    }
}
