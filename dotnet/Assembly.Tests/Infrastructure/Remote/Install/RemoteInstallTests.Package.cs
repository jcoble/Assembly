using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Remote.Install;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed partial class RemoteInstallTests
{
    [Fact]
    public async Task Actual_archive_validation_checks_closure_and_copied_selection_is_independent_of_source()
    {
        var file = await Bundle();
        var original = await RemoteInstallPackage.InspectAsync(file, default);
        using var snapshot = await RemoteInstallPackage.CopyAsync(Staging, file, original.Bundle.Sha256, default);
        await File.WriteAllTextAsync(file, "replaced source");
        var copied = await RemoteInstallPackage.InspectAsync(snapshot.File, default);
        Assert.Equal(original.Bundle, copied.Bundle); Assert.Equal(3, copied.Manifest.Files.Length);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("traversal")]
    [InlineData("hash")]
    [InlineData("format")]
    public async Task Unsafe_or_mismatched_bundle_is_refused_before_any_remote_work(string corruption)
    {
        var file = await Bundle(corruption);
        var error = await Assert.ThrowsAsync<CallFailed>(() => RemoteInstallPackage.InspectAsync(file, default));
        Assert.Equal("invalid_bundle", error.Reason); Assert.Equal(0, _process.Stages);
    }

    private async Task<string> Bundle(string? corruption = null)
    {
        var runtime = Encoding.UTF8.GetBytes("""{"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}""");
        var files = new Dictionary<string, byte[]>
        {
            ["backend/Assembly.Remote.dll"] = "inert test assembly"u8.ToArray(),
            ["backend/Assembly.Remote.deps.json"] = "{}"u8.ToArray(),
            ["backend/Assembly.Remote.runtimeconfig.json"] = runtime
        };
        var manifest = new RemoteInstallManifest(1, "assembly-dotnet-remote-backend-dev", "development", "linux-x64", new('a', 40), 1,
            corruption == "format" ? 99 : 5, [new("Microsoft.NETCore.App", "10.0.0"), new("Microsoft.AspNetCore.App", "10.0.0")],
            [.. files.Select(pair => new RemoteInstallFile(pair.Key, pair.Value.Length,
                corruption == "hash" ? new('b', 64) : Convert.ToHexStringLower(SHA256.HashData(pair.Value)), "0644"))]);
        var path = Path.Combine(_home, Guid.NewGuid().ToString("N") + ".tar.gz");
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true);
        await tar.WriteEntryAsync(new UstarTarEntry(TarEntryType.RegularFile, "manifest.json")
        { DataStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, RemoteInstallJson.Default.RemoteInstallManifest)), Mode = (UnixFileMode)420 });
        foreach (var (name, bytes) in files)
        {
            var entry = new UstarTarEntry(corruption == "link" ? TarEntryType.SymbolicLink : TarEntryType.RegularFile,
                corruption == "traversal" ? "../outside" : name)
            { Mode = (UnixFileMode)420 };
            if (corruption == "link") entry.LinkName = "/private/config"; else entry.DataStream = new MemoryStream(bytes);
            await tar.WriteEntryAsync(entry);
        }
        return path;
    }
}
