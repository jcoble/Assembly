using System.Text.RegularExpressions;

using Assembly.Application.Features.Chat.Attachments;
namespace Assembly.Infrastructure.Chat.Attachments;

/// <summary>
/// Images the composer attaches, one folder per thread, each under a fresh name. The prompt carries the file's
/// path, and the agent reads the file itself. Only names this store made are served back.
/// </summary>
public sealed partial class AttachmentStore
{
    public const int MaxBytes = 20 * 1024 * 1024;

    private readonly string _root;

    public AttachmentStore(string root) => _root = Path.GetFullPath(root);

    /// <summary>Saves the image and returns its absolute path.</summary>
    public async Task<string> SaveAsync(string threadId, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(_root, threadId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Guid.NewGuid()}.{type.Extension}");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    /// <summary>The image's type if <paramref name="name"/> could be one of our names, else null.</summary>
    public static ImageType? TypeOfName(string name) =>
        OurName().Match(name) is { Success: true } match ? ImageType.ForExtension(match.Groups[1].Value) : null;

    public string PathOf(string threadId, string name) => Path.Combine(_root, threadId, name);

    [GeneratedRegex("^[0-9a-f-]{36}\\.(png|jpg|gif|webp)$")]
    private static partial Regex OurName();
}
