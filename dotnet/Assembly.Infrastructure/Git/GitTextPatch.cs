using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Git;
using Assembly.Application.Features.Chat.SourceControl;
namespace Assembly.Infrastructure.Git;

/// <summary>One bounded supplied-version comparison. Git owns differences; temporary files never touch a checkout.</summary>
public sealed class GitTextPatch(IGit git) : IGitTextPatch
{
    public async Task<GitFileDiff> ReadAsync(string path, string? originalPath, ReadOnlyMemory<byte>? original, ReadOnlyMemory<byte>? modified, CancellationToken ct)
    {
        ValidatePath(path); ValidatePath(originalPath ?? path); ct.ThrowIfCancellationRequested();
        if (original is null && modified is null) throw new CallFailed("Both file versions are missing.");
        if (IsBinary(original) || IsBinary(modified)) return new(path, "", "", true, false);
        var temporary = Directory.CreateTempSubdirectory("assembly-text-patch-");
        try
        {
            var oldName = original is null ? "/dev/null" : await Save(temporary.FullName, "a", originalPath ?? path, original.Value, ct);
            var newName = modified is null ? "/dev/null" : await Save(temporary.FullName, "b", path, modified.Value, ct);
            var patch = await GitDiffReader.ReadText(git, temporary.FullName,
                ["-c", "core.quotepath=false", "diff", "--no-index", "--no-ext-diff", "--no-textconv", "--no-prefix", "--unified=2147483647", "--", oldName, newName], GitDiffReader.Cap, ct);
            return new(path, "", patch.Text, false, patch.Truncated);
        }
        finally { temporary.Delete(true); }
    }
    void IGitTextPatch.ValidatePath(string path) => ValidatePath(path);

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\0') || path.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new CallFailed("Invalid comparison file path.");
    }
    private static bool IsBinary(ReadOnlyMemory<byte>? bytes)
    {
        if (bytes is null) return false;
        if (bytes.Value.Span.Contains((byte)0)) return true;
        try { new UTF8Encoding(false, true).GetCharCount(bytes.Value.Span); return false; }
        catch (DecoderFallbackException) { return true; }
    }
    private static async Task<string> Save(string directory, string side, string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var relative = side + "/" + path;
        var destination = Path.GetFullPath(Path.Combine(directory, relative));
        if (!destination.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new CallFailed("Invalid comparison file path.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var pending = destination + ".assembly-writing-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(pending, bytes.ToArray(), ct);
        File.Move(pending, destination);
        return relative;
    }
}
