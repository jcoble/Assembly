using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Assembly.DevSeed;

public static partial class SeedFiles
{
    public const long MaxInputBytes = 64 * 1024 * 1024;
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static bool Within(string path, string root) => path == root || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    public static string SafePath(string path, bool mustExist = true)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split(Path.DirectorySeparatorChar).Any(p => p is "." or ".."))
            throw new InvalidDataException("Expected absolute path without traversal.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var at = full; at is not null; at = Path.GetDirectoryName(at))
            if (new FileInfo(at).LinkTarget is not null)
                throw new InvalidDataException("Symlink paths are refused.");
        if (mustExist && !File.Exists(full) && !Directory.Exists(full))
            throw new InvalidDataException("Explicit input does not exist.");
        var existing = full;
        var suffix = new Stack<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            suffix.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing) ?? throw new InvalidDataException("No existing path ancestor.");
        }
        var pointer = RealPath(existing, IntPtr.Zero);
        if (pointer == IntPtr.Zero) throw new IOException("Cannot canonicalize existing path ancestor.");
        string canonical;
        try { canonical = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(pointer) ?? throw new IOException("Invalid canonical path."); }
        finally { FreePath(pointer); }
        while (suffix.Count > 0) canonical = Path.Combine(canonical, suffix.Pop());
        return Path.TrimEndingDirectorySeparator(canonical);
    }
    public static string Input(string path, long maximum = MaxInputBytes)
    {
        path = SafePath(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > maximum)
            throw new InvalidDataException("Input is not a bounded regular file.");
        // The managed APIs expose symlinks but not Unix link counts or special-file type.
        var info = new ProcessStartInfo("stat") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-f" : "-c");
        info.ArgumentList.Add(OperatingSystem.IsMacOS() ? "%l:%HT" : "%h:%F");
        info.ArgumentList.Add(path);
        using var process = Process.Start(info) ?? throw new IOException("Cannot validate file identity.");
        var result = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || !(result is "1:regular file" or "1:regular empty file" or "1:Regular File"))
            throw new InvalidDataException("Hardlinks and special input files are refused.");
        return path;
    }
    public static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Split('/').Any(p => p is "" or "." or ".." || p.StartsWith('.') || Forbidden().IsMatch(p)))
            throw new InvalidDataException("Unsafe asset relative path (hidden/config/provider trees are refused).");
        return path;
    }
    [GeneratedRegex("(?i)(credential|secret|token|password|agents?\\.json|claude|codex|config|id_rsa|id_ed25519)")]
    private static partial Regex Forbidden();
    public static void PrivateDirectory(string path)
    {
        SafePath(path, false);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static void AtomicJson<T>(string path, T value)
    {
        SafePath(path, false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var bytes = Encoding.UTF8.GetBytes(SeedJson.Stringify(value));
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, path, true);
            SyncDirectory(Path.GetDirectoryName(path)!);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial IntPtr RealPath(string path, IntPtr resolved);
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "free")]
    private static partial void FreePath(IntPtr path);
    public static bool OverlapsHome(IEnumerable<string> sources, string home)
    {
        if (sources.Any(path => Within(path, home) || Within(home, path))) return true;
        if (!Directory.Exists(home)) return false;
        string Identity(string path)
        {
            var start = new ProcessStartInfo("stat") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-f" : "-c"); start.ArgumentList.Add("%d:%i"); start.ArgumentList.Add(path);
            using var process = Process.Start(start) ?? throw new IOException("Cannot compare physical path identity.");
            var identity = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Cannot compare physical path identity.");
            return identity;
        }
        var targetIdentity = Identity(home);
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
            for (var at = Path.GetDirectoryName(source); at is not null; at = Path.GetDirectoryName(at)) ancestors.Add(at);
        return ancestors.Any(ancestor => Directory.Exists(ancestor) && Identity(ancestor) == targetIdentity);
    }
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "open", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int OpenDirectory(string path, int flags);
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "fsync")]
    private static partial int Fsync(int fd);
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
    public static void SyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var fd = OpenDirectory(path, 0);
        if (fd < 0) throw new IOException("Cannot open directory for durable seed publication.");
        try { if (Fsync(fd) != 0) throw new IOException("Cannot sync seed publication directory."); }
        finally { Close(fd); }
    }
    public static string Profile(string worktree, string? userHome = null)
    {
        worktree = SafePath(worktree);
        foreach (var marker in new[] { "desktop/Cargo.toml", "dotnet/Assembly.Desktop/Assembly.Desktop.csproj" })
            if (!File.Exists(Path.Combine(worktree, marker))) throw new InvalidDataException("Not an Assembly worktree.");
        var name = Regex.Replace(Path.GetFileName(worktree), "[^a-zA-Z0-9-]", "-");
        return Path.Combine(userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "assembly-dev", "worktrees", name + "-" + Hash(worktree)[..16]);
    }
    public static string RemoteProfile(string worktree, SeedRemoteLane lane, string userHome)
    {
        if (!Regex.IsMatch(lane.Name, "^[a-z0-9][a-z0-9-]*$") || lane.Number is < 1 or > 9 ||
            new[] { "main", "base", "rail", "remote", "assembly-dotnet", "assembly-dotnet-base", "assembly-dotnet-rail", "assembly-remote" }.Contains(lane.Name))
            throw new InvalidDataException("Remote seed refuses main or protected lane names.");
        var root = Path.Combine(userHome, "assembly-dotnet-" + lane.Name);
        if (worktree != Path.Combine(root, "checkouts", "build") || File.ReadAllText(Path.Combine(root, "lane-number")).Trim() != lane.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidDataException("Remote seed must use the lane-owned checkout and reservation.");
        _ = Profile(worktree, userHome);
        return Path.Combine(root, "serve");
    }
    public static void CheckAsset(SeedAsset asset, bool attachment, string? attachmentRoot)
    {
        Relative(asset.RelativePath);
        var input = Input(asset.SourcePath, attachment ? Assembly.Infrastructure.Chat.Attachments.AttachmentStore.MaxBytes : 8 * 1024 * 1024);
        if (input.Split(Path.DirectorySeparatorChar).Any(component =>
            component is ".git" or ".ssh" or ".aws" or ".config" or ".claude" or ".codex" or ".npmrc" or ".netrc" or ".env" or ".pypirc" || Forbidden().IsMatch(component)))
            throw new InvalidDataException("Provider, configuration and credential asset sources are refused.");
        if (!string.Equals(HashFile(input), asset.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("Asset hash mismatch.");
        if (!attachment) return;
        if (attachmentRoot is null || !Within(input, SafePath(attachmentRoot))) throw new InvalidDataException("Attachment is outside approved root.");
        if (Assembly.Infrastructure.Chat.Attachments.AttachmentStore.TypeOfName(asset.RelativePath) is null ||
            !Guid.TryParse(Path.GetFileNameWithoutExtension(asset.RelativePath), out _) ||
            asset.RelativePath != Path.GetFileName(asset.RelativePath) ||
            Path.GetExtension(asset.RelativePath) is not (".png" or ".jpg" or ".gif" or ".webp") || string.IsNullOrEmpty(asset.OriginalPath))
            throw new InvalidDataException("Invalid attachment name/type/reference.");
        var type = Assembly.Infrastructure.Chat.Attachments.AttachmentStore.TypeOfName(asset.RelativePath)!;
        if (!type.Matches(File.ReadAllBytes(input))) throw new InvalidDataException("Attachment bytes do not match image type.");
    }
}
