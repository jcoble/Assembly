using Assembly.Application.Common.Images;
using System.Text.RegularExpressions;

using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Attachments;
namespace Assembly.Infrastructure.Chat.Attachments;

/// <summary>
/// Images the composer attaches, one folder per session, each under a fresh name. The prompt carries the file's
/// path, and the agent reads the file itself. Only names this store made are served back.
/// </summary>
public sealed partial class AttachmentStore
{
    public const int MaxBytes = 20 * 1024 * 1024;

    private readonly string _root;
    private readonly Func<CancellationToken, Task>? _beforePublish;

    public AttachmentStore(string root) : this(root, null) { }

    // Only the atomic-store tests pause or fail this save immediately before publication.
    internal AttachmentStore(string root, Func<CancellationToken, Task>? beforePublish) =>
        (_root, _beforePublish) = (Path.GetFullPath(root), beforePublish);

    /// <summary>Saves the image and returns its absolute path.</summary>
    public async Task<string> SaveAsync(string sessionId, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Guid.NewGuid()}.{type.Extension}");
        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.tmp");
        var owned = false;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                owned = true;
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            if (_beforePublish is not null) await _beforePublish(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            return path;
        }
        finally { if (owned) File.Delete(temporary); }
    }

    /// <summary>Reads at most one image chunk from this store, preserving the original size and MIME type.</summary>
    public async Task<AttachmentPiece> ReadAsync(string sessionId, string name, long offset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var type = TypeOfName(name) ?? throw new CallFailed("not an attachment name");
        if (string.IsNullOrWhiteSpace(sessionId) || Path.GetFileName(sessionId) != sessionId || sessionId is "." or "..")
            throw new CallFailed("no such attachment");
        var path = PathOf(sessionId, name);
        if (!File.Exists(path)) throw new CallFailed("no such attachment");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        var size = file.Length;
        if (offset < 0 || offset > size) throw new CallFailed("the offset is outside the attachment");
        file.Position = offset;
        var data = new byte[Math.Min(AttachmentsFeature.ChunkBytes, size - offset)];
        await file.ReadExactlyAsync(data, ct);
        return new AttachmentPiece(data, size, type.ContentType);
    }

    /// <summary>The image's type if <paramref name="name"/> could be one of our names, else null.</summary>
    public static ImageType? TypeOfName(string name) =>
        OurName().Match(name) is { Success: true } match ? ImageType.ForExtension(match.Groups[1].Value) : null;

    public string PathOf(string sessionId, string name) => Path.Combine(_root, sessionId, name);

    /// <summary>Checks only a reference's owning upload path; no file content or existence I/O.</summary>
    public bool Owns(string sessionId, string reference)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || Path.GetFileName(sessionId) != sessionId || sessionId is "." or ".." ||
            string.IsNullOrWhiteSpace(reference)) return false;
        var name = Path.GetFileName(reference);
        return TypeOfName(name) is not null && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "D", out _) &&
            string.Equals(reference, PathOf(sessionId, name), StringComparison.Ordinal);
    }


    [GeneratedRegex("^[0-9a-f-]{36}\\.(png|jpg|gif|webp)$")]
    private static partial Regex OurName();
}
