using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Remote.Install;

internal sealed record RemoteReleaseRecord(int SchemaVersion, string Version, int ProtocolVersion, string Target, string ArchiveFile, long Bytes, string Sha256);
/// <summary>An adapter release whose signed manifest passed. A remote fetches it by this tag and refuses any other manifest.</summary>
internal sealed record VerifiedAdapters(string Tag, string ManifestSha256);
/// <summary>A release whose signed files passed. The archive URL, record and adapters are what a remote is told to fetch and check.</summary>
internal sealed record VerifiedRemoteRelease(string ArchiveUrl, RemoteReleaseRecord Record, VerifiedAdapters Adapters);

/// <summary>
/// Finds the latest published release and adapters and verifies their signed files here, on the desktop, which holds the
/// pinned key. Only these small files come to the desktop: the remote downloads the archives itself.
/// </summary>
internal sealed partial class RemoteReleaseFeed(HttpClient http, string publicKey = RemoteReleaseTrust.SigningPublicKey)
{
    internal const string Target = "x86_64-unknown-linux-gnu";
    internal const long MaxArchive = 512L * 1024 * 1024;
    private const string Endpoint = "https://api.github.com/repos/jcoble/Assembly/releases?per_page=100";
    private const string TagPrefix = "assembly-v";
    private const string AdapterArchive = "assembly-adapters-linux-x64.tar.gz", AdapterManifest = "assembly-adapters-linux-x64.json";
    private const string AssetPrefix = "/jcoble/Assembly/releases/download/";
    private const int MaxSmallAsset = 16 * 1024, MaxListing = 8 * 1024 * 1024;

    private sealed record Asset(string Name, long Size, string Url);

    internal async Task<VerifiedRemoteRelease> LatestAsync(CancellationToken ct)
    {
        using var listing = JsonDocument.Parse(await GetAsync(Endpoint, MaxListing, "Could not check public backend releases", ct));
        var release = listing.RootElement.EnumerateArray().FirstOrDefault(item =>
            !item.GetProperty("draft").GetBoolean() && !item.GetProperty("prerelease").GetBoolean() &&
            item.GetProperty("tag_name").GetString() is { } tag && tag.StartsWith(TagPrefix, StringComparison.Ordinal));
        if (release.ValueKind == JsonValueKind.Undefined) throw Failure("No published Assembly backend release is available.", "release_missing");
        var tag = release.GetProperty("tag_name").GetString()!;
        var version = tag[TagPrefix.Length..];
        if (!StableVersion().IsMatch(version)) throw Failure($"Invalid Assembly backend release tag: {tag}", "release_invalid");
        var assets = Assets(release);
        var archiveName = $"assembly-remote-backend-{version}-{Target}.tar.gz";
        var archive = Find(assets, archiveName) ?? throw Failure($"{tag} has no Linux x86-64 backend package.", "release_incomplete");
        var record = Find(assets, archiveName + ".download.json") ?? throw Failure($"{tag} lacks the signed download record; publish a compatible backend release.", "release_incomplete");
        var signature = Find(assets, archiveName + ".download.json.sig") ?? throw Failure($"{tag} lacks the signed download record signature.", "release_incomplete");
        var recordBytes = await SmallAsync(record, ct);
        var signatureBytes = await SmallAsync(signature, ct);
        RemoteReleaseTrust.Verify(recordBytes, signatureBytes, publicKey);
        RemoteReleaseRecord parsed;
        try { parsed = JsonSerializer.Deserialize(recordBytes, RemoteReleaseJson.Default.RemoteReleaseRecord) ?? throw Failure("Signed backend record is invalid.", "release_invalid"); }
        catch (JsonException) { throw Failure("Signed backend record is invalid.", "release_invalid"); }
        Validate(parsed, archive, version);
        RequirePublicUrl(archive.Url);
        return new(archive.Url, parsed, await AdaptersAsync(listing.RootElement, ct));
    }

    // An unsigned adapter release never qualifies, so the newest signed one is the answer.
    private async Task<VerifiedAdapters> AdaptersAsync(JsonElement releases, CancellationToken ct)
    {
        var release = releases.EnumerateArray()
            .Where(item => !item.GetProperty("draft").GetBoolean() && !item.GetProperty("prerelease").GetBoolean() &&
                AdapterTag().IsMatch(item.GetProperty("tag_name").GetString() ?? ""))
            .OrderByDescending(item => item.GetProperty("published_at").GetDateTimeOffset())
            .Select(item => (Tag: item.GetProperty("tag_name").GetString()!, Assets: Assets(item)))
            .FirstOrDefault(item => Find(item.Assets, AdapterArchive) is not null && Find(item.Assets, AdapterManifest) is not null && Find(item.Assets, AdapterManifest + ".sig") is not null);
        if (release.Tag is null) throw Failure("No signed adapter release is available.", "release_missing");
        var manifest = await SmallAsync(Find(release.Assets, AdapterManifest)!, ct);
        var signature = await SmallAsync(Find(release.Assets, AdapterManifest + ".sig")!, ct);
        RemoteReleaseTrust.Verify(manifest, signature, publicKey);
        return new(release.Tag, Convert.ToHexStringLower(SHA256.HashData(manifest)));
    }

    private static Asset[] Assets(JsonElement release) => release.GetProperty("assets").EnumerateArray().Select(asset =>
        new Asset(asset.GetProperty("name").GetString()!, asset.GetProperty("size").GetInt64(), asset.GetProperty("browser_download_url").GetString()!)).ToArray();

    private static void Validate(RemoteReleaseRecord record, Asset archive, string version)
    {
        if (record.SchemaVersion != 1 || record.Version != version || record.Target != Target || record.ArchiveFile != archive.Name ||
            record.Bytes != archive.Size || record.Bytes <= 0 || record.Bytes > MaxArchive || !RemoteInstallPackage.Hex(record.Sha256, 64))
            throw Failure("Signed backend download record does not match this release.", "release_invalid");
        var client = SocketConnection.Protocol;
        if (record.ProtocolVersion < client)
            throw Failure($"The latest published backend ({record.Version}) uses protocol {record.ProtocolVersion}, but this Assembly requires {client}. A matching backend release has not been published yet. Check for backend updates after the release build finishes; reinstalling the current release will not help.", "protocol_older");
        if (record.ProtocolVersion > client)
            throw Failure($"The latest backend ({record.Version}) requires protocol {record.ProtocolVersion}, but this Assembly uses {client}. Update Assembly before installing this backend.", "protocol_newer");
    }

    private static Asset? Find(Asset[] assets, string name) => assets.FirstOrDefault(asset => asset.Name == name);

    private async Task<byte[]> SmallAsync(Asset asset, CancellationToken ct)
    {
        if (asset.Size <= 0 || asset.Size > MaxSmallAsset) throw Failure($"Backend release record asset has an invalid size: {asset.Name}", "release_invalid");
        RequirePublicUrl(asset.Url);
        var bytes = await GetAsync(asset.Url, MaxSmallAsset, $"Could not download {asset.Name}", ct);
        if (bytes.Length != asset.Size) throw Failure($"Backend release asset was incomplete: {asset.Name}", "release_invalid");
        return bytes;
    }

    private static void RequirePublicUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" ||
            !url.AbsolutePath.StartsWith(AssetPrefix, StringComparison.Ordinal))
            throw Failure("Backend asset URL is not a public HTTPS release URL.", "release_invalid");
    }

    private async Task<byte[]> GetAsync(string url, int limit, string label, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Assembly-backend-installer");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw Failure($"{label}: HTTP {(int)response.StatusCode}.", "release_unavailable");
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await body.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (output.Length + count > limit) throw Failure($"{label}: the response is too large.", "release_invalid");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (HttpRequestException error) { throw Failure($"{label}: {error.Message}", "release_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure($"{label}: timed out.", "release_unavailable"); }
    }

    private static CallFailed Failure(string message, string reason) => new(message, reason);

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")]
    private static partial Regex StableVersion();

    [GeneratedRegex(@"^assembly-providers-[0-9]+-[0-9]+$")]
    private static partial Regex AdapterTag();
}
