using System.Runtime.InteropServices;
using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Chat.Files;

/// <summary>Checkout-contained actions. Copies publish one private staged target; moves never copy/delete across devices.</summary>
public static class FileActions
{
    public static async Task<ActionResult> ExecuteAsync(string root, FileActionArgs args, CancellationToken ct, Action? validateOwner = null)
    {
        ct.ThrowIfCancellationRequested();
        validateOwner?.Invoke();
        var source = SessionPaths.ResolveFile(root, args.Path);
        var destination = args.Destination is null ? null : SessionPaths.ResolveFile(root, args.Destination);
        var paths = new[] { source, destination }.OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var locks = new List<IDisposable>();
        try
        {
            foreach (var path in paths) locks.Add(await FileMutationOrder.EnterAsync(path, ct));
            source = SessionPaths.ResolveFile(root, args.Path);
            if (destination is not null) destination = SessionPaths.ResolveFile(root, args.Destination!);
            if (args.Kind is "createFile" or "createFolder")
                return await CreateAsync(source, args.Path, args.Kind == "createFolder", ct, validateOwner);
            if (!File.Exists(source) && !Directory.Exists(source)) return Failed("missing", "The source item is missing.");
            if (args.Revision is not null && File.Exists(source) && await FileSave.RevisionAsync(source, ct) != args.Revision)
                return Failed("revision_changed", "The source file changed on disk.");
            if (args.Kind == "trash")
            {
                validateOwner?.Invoke();
                var trashed = await new FileTrash().TrashAsync(source, ct);
                return trashed with { Path = args.Path };
            }
            if (args.Kind is not ("rename" or "move" or "copy") || destination is null)
                throw new CallFailed("invalid file action or missing destination");
            if (source == destination || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return Failed("invalid_destination", "An item cannot be copied or moved into itself.");
            if (File.Exists(destination) || Directory.Exists(destination)) return Failed("collision", "The destination already exists.");
            if (!Directory.Exists(Path.GetDirectoryName(destination))) return Failed("missing", "The destination folder is missing.");
            if (args.Kind is "rename" or "move")
            {
                ct.ThrowIfCancellationRequested();
                validateOwner?.Invoke();
                var fault = MoveExclusive(source, destination);
                return fault is null ? new ActionResult("applied", args.Destination, null) : new ActionResult("failed", null, fault);
            }
            var stage = Path.Combine(Path.GetDirectoryName(destination)!, ".assembly-action-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (Directory.Exists(source)) await CopyDirectoryAsync(root, source, stage, ct);
                else await CopyFileAsync(source, stage, ct);
                ct.ThrowIfCancellationRequested();
                SessionPaths.ResolveFile(root, args.Path);
                SessionPaths.ResolveFile(root, args.Destination!);
                validateOwner?.Invoke();
                var fault = MoveExclusive(stage, destination);
                return fault is null ? new ActionResult("applied", args.Destination, null) : new ActionResult("failed", null, fault);
            }
            finally { RemoveStage(stage); }
        }
        finally { for (var i = locks.Count - 1; i >= 0; i--) locks[i].Dispose(); }
    }

    private static async Task<ActionResult> CreateAsync(string target, string relative, bool directory, CancellationToken ct, Action? validateOwner)
    {
        if (File.Exists(target) || Directory.Exists(target)) return Failed("collision", "The destination already exists.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) return Failed("missing", "The destination folder is missing.");
        var stage = Path.Combine(Path.GetDirectoryName(target)!, ".assembly-action-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (directory) Directory.CreateDirectory(stage);
            else { await using var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None); await file.FlushAsync(ct); }
            ct.ThrowIfCancellationRequested();
            validateOwner?.Invoke();
            var fault = MoveExclusive(stage, target);
            return fault is null ? new ActionResult("applied", relative, null) : new ActionResult("failed", null, fault);
        }
        finally { RemoveStage(stage); }
    }

    private static async Task CopyFileAsync(string source, string stage, CancellationToken ct)
    {
        await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var to = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await from.CopyToAsync(to, ct);
        await to.FlushAsync(ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, File.GetUnixFileMode(source));
    }

    private static async Task CopyDirectoryAsync(string root, string source, string stage, CancellationToken ct)
    {
        Directory.CreateDirectory(stage);
        foreach (var info in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (info.LinkTarget is not null) throw new CallFailed("a copied directory contains a link");
            var contained = SessionPaths.ResolveFile(root, Path.GetRelativePath(root, info.FullName));
            var target = Path.Combine(stage, info.Name);
            if (info is DirectoryInfo) await CopyDirectoryAsync(root, contained, target, ct);
            else await CopyFileAsync(contained, target, ct);
        }
    }

    private static void RemoveStage(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static ActionResult Failed(string code, string message) => new("failed", null, new FileFault(code, message));

    private static FileFault? MoveExclusive(string from, string to)
    {
        // Linux stdio.h RENAME_NOREPLACE=1; Darwin renamex_np RENAME_EXCL=4 (libc's Apple declarations).
        // These rename-only calls preserve source on EXDEV and atomically refuse an external destination collision.
        int result;
        try
        {
            result = OperatingSystem.IsLinux() ? renameat2(-100, from, -100, to, 1) :
                OperatingSystem.IsMacOS() ? renamex_np(from, to, 4) : -1;
        }
        catch (EntryPointNotFoundException) { return new FileFault("unsupported", "This host cannot perform a safe exclusive move."); }
        if (result == 0) return null;
        var code = Marshal.GetLastPInvokeError();
        return code == 18 ? new FileFault("unsupported", "Moving between filesystems is not supported; the source is intact.") :
            code == 17 || File.Exists(to) || Directory.Exists(to) ? new FileFault("collision", "The destination already exists.") :
            new FileFault("move_failed", Marshal.GetLastPInvokeErrorMessage());
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int renameat2(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int renamex_np([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
}
