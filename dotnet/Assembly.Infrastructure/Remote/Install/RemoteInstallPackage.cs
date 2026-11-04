using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Assembly.Application.Common.Calls;

namespace Assembly.Infrastructure.Remote.Install;

internal static class RemoteInstallPackage
{
    internal const long MaxArchive = 512L * 1024 * 1024, MaxUnpacked = 1024L * 1024 * 1024;
    private const int MaxFiles = 4096, MaxManifest = 2 * 1024 * 1024;
    internal sealed record Checked(RemoteInstallBundle Bundle, RemoteInstallManifest Manifest);
    internal sealed class Snapshot(string directory, string file) : IDisposable
    {
        internal string File { get; } = file;
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    internal static async Task<Snapshot> CopyAsync(string stagingRoot, string path, string expected, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(stagingRoot)) throw Invalid();
        Directory.CreateDirectory(stagingRoot);
        if ((System.IO.File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        var directory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Path.Combine(directory, "bundle.tar.gz");
        try
        {
            if (!Path.IsPathFullyQualified(path) || (System.IO.File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw Invalid();
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var output = new FileStream(file, options);
            if (input.Length is <= 0 or > MaxArchive) throw Invalid();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long bytes = 0; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                bytes += count; if (bytes > MaxArchive) throw Invalid();
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected) throw new CallFailed("The selected bundle changed.", "bundle_changed");
            await output.FlushAsync(ct);
            return new(directory, file);
        }
        catch { Directory.Delete(directory, recursive: true); throw; }
    }

    internal static async Task<Checked> InspectAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw Invalid();
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw Invalid();
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (source.Length is <= 0 or > MaxArchive) throw Invalid();
            var bytes = source.Length;
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, ct));
            source.Position = 0;
            await using var compressed = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
            await using var reader = new TarReader(compressed, leaveOpen: true);
            var observed = new Dictionary<string, RemoteInstallFile>(StringComparer.Ordinal);
            byte[]? manifestBytes = null, runtimeBytes = null;
            long total = 0;
            while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                    !SafePath(entry.Name) || observed.Count >= MaxFiles || entry.Length < 0 || entry.Length > MaxUnpacked - total ||
                    entry.DataStream is null || observed.ContainsKey(entry.Name)) throw Invalid();
                total += entry.Length;
                var mode = (int)entry.Mode;
                if (mode is not (420 or 493)) throw Invalid();
                var retain = entry.Name is "manifest.json" or "backend/Assembly.Remote.runtimeconfig.json";
                if (entry.Name != "manifest.json" && !entry.Name.StartsWith("backend/", StringComparison.Ordinal)) throw Invalid();
                if (retain && entry.Length > (entry.Name == "manifest.json" ? MaxManifest : 65536)) throw Invalid();
                using var kept = retain ? new MemoryStream() : null;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long read = 0;
                int count;
                while ((count = await entry.DataStream.ReadAsync(buffer, ct)) > 0)
                {
                    read += count;
                    if (read > entry.Length) throw Invalid();
                    hash.AppendData(buffer, 0, count);
                    if (kept is not null) await kept.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (read != entry.Length) throw Invalid();
                observed.Add(entry.Name, new(entry.Name, read, Convert.ToHexStringLower(hash.GetHashAndReset()), mode == 420 ? "0644" : "0755"));
                if (entry.Name == "manifest.json") manifestBytes = kept!.ToArray();
                if (entry.Name == "backend/Assembly.Remote.runtimeconfig.json") runtimeBytes = kept!.ToArray();
            }
            if (source.Length != bytes || manifestBytes is null || runtimeBytes is null) throw Invalid();
            var manifest = JsonSerializer.Deserialize(manifestBytes, RemoteInstallJson.Default.RemoteInstallManifest) ?? throw Invalid();
            Validate(manifest, observed, runtimeBytes);
            return new(new(digest, bytes, manifest.Commit, manifest.Target, manifest.Protocol, manifest.Format, manifest.BuildKind), manifest);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        { throw Invalid(); }
    }

    private static void Validate(RemoteInstallManifest manifest, Dictionary<string, RemoteInstallFile> observed, byte[] runtime)
    {
        if (manifest.SchemaVersion != 1 || manifest.PackageType != "assembly-dotnet-remote-backend-dev" ||
            manifest.BuildKind != "development" || manifest.Target != "linux-x64" || manifest.Protocol != 1 || manifest.Format != 5 ||
            !Hex(manifest.Commit, 40) || manifest.Files is not { Length: > 0 and < MaxFiles } || manifest.Frameworks is null || manifest.Frameworks.Any(value => value is null) ||
            observed.Count != manifest.Files.Length + 1) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file is null || !SafePath(file.Path) || !file.Path.StartsWith("backend/", StringComparison.Ordinal) ||
                !seen.Add(file.Path) || !Hex(file.Sha256, 64) || file.Bytes < 0 || file.Mode is not ("0644" or "0755") ||
                !observed.TryGetValue(file.Path, out var actual) || actual != file) throw Invalid();
        }
        foreach (var required in new[] { "backend/Assembly.Remote.dll", "backend/Assembly.Remote.deps.json", "backend/Assembly.Remote.runtimeconfig.json" })
            if (!seen.Contains(required)) throw Invalid();
        using var document = JsonDocument.Parse(runtime);
        var frameworks = document.RootElement.GetProperty("runtimeOptions").GetProperty("frameworks").EnumerateArray()
            .Select(value => new RemoteInstallFramework(value.GetProperty("name").GetString()!, value.GetProperty("version").GetString()!)).ToArray();
        if (frameworks.Length != 2 || frameworks.Any(value => value.Name is not ("Microsoft.NETCore.App" or "Microsoft.AspNetCore.App") ||
                !Version.TryParse(value.Version, out var version) || version.Major != 10) ||
            frameworks.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            !frameworks.OrderBy(value => value.Name, StringComparer.Ordinal).SequenceEqual(manifest.Frameworks.OrderBy(value => value.Name, StringComparer.Ordinal))) throw Invalid();
    }

    internal static bool Hex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool SafePath(string? value) => value is { Length: > 0 and <= 240 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '+' or '/' or '-') &&
        value.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));
    private static CallFailed Invalid() => new("The selected development bundle is invalid or unavailable.", "invalid_bundle");
}
