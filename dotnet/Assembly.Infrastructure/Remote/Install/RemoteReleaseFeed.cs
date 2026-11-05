using System.Security.Cryptography;
using System.Text;
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
/// <summary>What's published, for the update check. A null is something that couldn't be read, which never counts as behind.</summary>
/// <summary>The app's archive for one version, and the signature its release's latest.json carries for it.</summary>
internal sealed record AppRelease(string Version, string ArchiveUrl, long Bytes, string Signature);

internal sealed record PublishedVersions(string? App, IReadOnlyDictionary<string, string>? Adapters, string? Packaging);

/// <summary>
/// Finds the latest published release and adapters and verifies their signed files here, on the desktop, which holds the
/// pinned key. Only these small files come to the desktop: the remote downloads the archives itself.
/// </summary>
internal sealed partial class RemoteReleaseFeed(HttpClient http, string publicKey = RemoteReleaseTrust.SigningPublicKey)
{
    internal const string Target = "x86_64-unknown-linux-gnu";
    internal const long MaxArchive = 512L * 1024 * 1024;
    private const long MaxApp = 1024L * 1024 * 1024;
    private const string AppPlatform = "darwin-aarch64-app";
    private const string Endpoint = "https://api.github.com/repos/jcoble/Assembly/releases?per_page=100";
    private const string TagPrefix = "assembly-v";
    private const string RemoteAdapters = "linux-x64", AppFeed = "latest.json";
    private const string AssetPrefix = "/jcoble/Assembly/releases/download/";
    private const int MaxSmallAsset = 16 * 1024, MaxListing = 8 * 1024 * 1024;

    private sealed record Asset(string Name, long Size, string Url);

    internal Task<VerifiedRemoteRelease> LatestAsync(CancellationToken ct) => FindAsync(null, ct);

    /// <summary>The release of exactly this version: a remote is put on this app's own number, whatever's newer.</summary>
    internal Task<VerifiedRemoteRelease> ForVersionAsync(string version, CancellationToken ct) => FindAsync(version, ct);

    private async Task<VerifiedRemoteRelease> FindAsync(string? version, CancellationToken ct)
    {
        using var listing = await ListingAsync(ct);
        var release = Published(listing.RootElement, version is null ? null : TagPrefix + version);
        if (release.ValueKind == JsonValueKind.Undefined)
            throw Failure(version is null ? "No published Assembly backend release is available." : $"Assembly {version} has no published release to install.", "release_missing");
        var tag = release.GetProperty("tag_name").GetString()!;
        var number = tag[TagPrefix.Length..];
        if (!StableVersion().IsMatch(number)) throw Failure($"Invalid Assembly backend release tag: {tag}", "release_invalid");
        var assets = Assets(release);
        var archiveName = $"assembly-remote-backend-{number}-{Target}.tar.gz";
        var archive = Find(assets, archiveName) ?? throw Failure($"{tag} has no Linux x86-64 backend package.", "release_incomplete");
        var record = Find(assets, archiveName + ".download.json") ?? throw Failure($"{tag} lacks the signed download record; publish a compatible backend release.", "release_incomplete");
        var signature = Find(assets, archiveName + ".download.json.sig") ?? throw Failure($"{tag} lacks the signed download record signature.", "release_incomplete");
        var recordBytes = await SmallAsync(record, ct);
        var signatureBytes = await SmallAsync(signature, ct);
        RemoteReleaseTrust.Verify(recordBytes, signatureBytes, publicKey);
        RemoteReleaseRecord parsed;
        try { parsed = JsonSerializer.Deserialize(recordBytes, RemoteReleaseJson.Default.RemoteReleaseRecord) ?? throw Failure("Signed backend record is invalid.", "release_invalid"); }
        catch (JsonException) { throw Failure("Signed backend record is invalid.", "release_invalid"); }
        Validate(parsed, archive, number);
        RequirePublicUrl(archive.Url);
        return new(archive.Url, parsed, (await AdaptersAsync(listing.RootElement, RemoteAdapters, ct)).Verified);
    }

    /// <summary>This Mac's app for exactly this version. The archive is only trusted once its signature has passed.</summary>
    internal async Task<AppRelease> AppReleaseAsync(string version, CancellationToken ct)
    {
        using var listing = await ListingAsync(ct);
        var release = Published(listing.RootElement, TagPrefix + version);
        if (release.ValueKind == JsonValueKind.Undefined) throw Failure($"Assembly {version} has no published release to install.", "release_missing");
        var assets = Assets(release);
        var feed = Find(assets, AppFeed) ?? throw Failure($"Assembly {version} has no {AppFeed}.", "release_incomplete");
        var archive = Find(assets, $"Assembly_{version}_aarch64.app.tar.gz") ?? throw Failure($"Assembly {version} has no app for this Mac.", "release_incomplete");
        try
        {
            using var json = JsonDocument.Parse(await SmallAsync(feed, ct));
            var platform = json.RootElement.GetProperty("platforms").GetProperty(AppPlatform);
            var url = platform.GetProperty("url").GetString();
            if (json.RootElement.GetProperty("version").GetString() != version || url != archive.Url || archive.Size <= 0 || archive.Size > MaxApp)
                throw Failure($"Assembly {version}'s {AppFeed} doesn't match its release.", "release_invalid");
            RequirePublicUrl(archive.Url);
            return new(version, archive.Url, archive.Size, platform.GetProperty("signature").GetString()!);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw Failure($"Assembly {version}'s {AppFeed} can't be read.", "release_invalid"); }
    }

    /// <summary>Downloads the app's archive to a file and checks it against the pinned key.</summary>
    internal async Task DownloadAppAsync(AppRelease app, string file, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, app.ArchiveUrl);
            request.Headers.UserAgent.ParseAdd("Assembly-backend-installer");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw Failure($"Could not download Assembly {app.Version}: HTTP {(int)response.StatusCode}.", "release_unavailable");
            await using (var output = File.Create(file))
            await using (var body = await response.Content.ReadAsStreamAsync(ct))
                await body.CopyToAsync(output, ct);
        }
        catch (HttpRequestException error) { throw Failure($"Could not download Assembly {app.Version}: {error.Message}", "release_unavailable"); }
        if (new FileInfo(file).Length != app.Bytes) throw Failure($"The download of Assembly {app.Version} was incomplete.", "release_invalid");
        RemoteReleaseTrust.Verify(await File.ReadAllBytesAsync(file, ct), Encoding.UTF8.GetBytes(app.Signature), publicKey);
    }

    /// <summary>The newest signed adapters for one platform, for this machine's own install.</summary>
    internal async Task<VerifiedAdapters> AdaptersForAsync(string target, CancellationToken ct)
    {
        using var listing = await ListingAsync(ct);
        return (await AdaptersAsync(listing.RootElement, target, ct)).Verified;
    }

    /// <summary>The app's wanted version from the newest release's latest.json, and the newest signed adapters' versions and packaging.</summary>
    internal async Task<PublishedVersions> PublishedAsync(CancellationToken ct)
    {
        try
        {
            using var listing = await ListingAsync(ct);
            var app = await Unknown(() => AppVersionAsync(listing.RootElement, ct));
            var adapters = await Unknown(async () => AdapterFacts((await AdaptersAsync(listing.RootElement, RemoteAdapters, ct)).Manifest));
            return new(app, adapters?.Versions, adapters?.Packaging);
        }
        catch (Exception error) when (error is CallFailed or JsonException or InvalidOperationException or KeyNotFoundException) { return new(null, null, null); }

        static async Task<T?> Unknown<T>(Func<Task<T?>> read) where T : class
        {
            try { return await read(); }
            catch (Exception error) when (error is CallFailed or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
        }
    }

    private async Task<string?> AppVersionAsync(JsonElement releases, CancellationToken ct)
    {
        var release = Published(releases, null);
        if (release.ValueKind == JsonValueKind.Undefined) return null;
        var feed = Find(Assets(release), AppFeed);
        if (feed is null) return null;
        using var json = JsonDocument.Parse(await SmallAsync(feed, ct));
        var version = json.RootElement.GetProperty("version").GetString();
        // The tag and the feed come from one job, so a disagreement is a release that can't be trusted to say.
        return version is not null && TagPrefix + version == release.GetProperty("tag_name").GetString() && StableVersion().IsMatch(version) ? version : null;
    }

    private sealed record AdapterVersions(IReadOnlyDictionary<string, string> Versions, string? Packaging);

    private static AdapterVersions AdapterFacts(byte[] manifest)
    {
        using var json = JsonDocument.Parse(manifest);
        var versions = json.RootElement.GetProperty("versions").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal);
        return new(versions, json.RootElement.TryGetProperty("packaging", out var packaging) ? packaging.GetString() : null);
    }

    private async Task<JsonDocument> ListingAsync(CancellationToken ct) =>
        JsonDocument.Parse(await GetAsync(Endpoint, MaxListing, "Could not check public backend releases", ct));

    // The newest published release of Assembly itself, or the one with exactly this tag.
    private static JsonElement Published(JsonElement releases, string? exactTag) => releases.EnumerateArray().FirstOrDefault(item =>
        !item.GetProperty("draft").GetBoolean() && !item.GetProperty("prerelease").GetBoolean() &&
        item.GetProperty("tag_name").GetString() is { } tag && (exactTag is null ? tag.StartsWith(TagPrefix, StringComparison.Ordinal) : tag == exactTag));

    // An unsigned adapter release never qualifies, so the newest signed one is the answer.
    private async Task<(VerifiedAdapters Verified, byte[] Manifest)> AdaptersAsync(JsonElement releases, string target, CancellationToken ct)
    {
        string adapterArchive = $"assembly-adapters-{target}.tar.gz", adapterManifest = $"assembly-adapters-{target}.json";
        var release = releases.EnumerateArray()
            .Where(item => !item.GetProperty("draft").GetBoolean() && !item.GetProperty("prerelease").GetBoolean() &&
                AdapterTag().IsMatch(item.GetProperty("tag_name").GetString() ?? ""))
            .OrderByDescending(item => item.GetProperty("published_at").GetDateTimeOffset())
            .Select(item => (Tag: item.GetProperty("tag_name").GetString()!, Assets: Assets(item)))
            .FirstOrDefault(item => Find(item.Assets, adapterArchive) is not null && Find(item.Assets, adapterManifest) is not null && Find(item.Assets, adapterManifest + ".sig") is not null);
        if (release.Tag is null) throw Failure("No signed adapter release is available.", "release_missing");
        var manifest = await SmallAsync(Find(release.Assets, adapterManifest)!, ct);
        var signature = await SmallAsync(Find(release.Assets, adapterManifest + ".sig")!, ct);
        RemoteReleaseTrust.Verify(manifest, signature, publicKey);
        return (new(release.Tag, Convert.ToHexStringLower(SHA256.HashData(manifest))), manifest);
    }

    private static Asset[] Assets(JsonElement release) => release.GetProperty("assets").EnumerateArray().Select(asset =>
        new Asset(asset.GetProperty("name").GetString()!, asset.GetProperty("size").GetInt64(), asset.GetProperty("browser_download_url").GetString()!)).ToArray();

    private static void Validate(RemoteReleaseRecord record, Asset archive, string version)
    {
        if (record.SchemaVersion != 1 || record.Version != version || record.Target != Target || record.ArchiveFile != archive.Name ||
            record.Bytes != archive.Size || record.Bytes <= 0 || record.Bytes > MaxArchive || !Hex(record.Sha256, 64))
            throw Failure("Signed backend download record does not match this release.", "release_invalid");
        var client = SocketConnection.Protocol;
        if (record.ProtocolVersion < client)
            throw Failure($"The latest published backend ({record.Version}) uses protocol {record.ProtocolVersion}, but this Assembly requires {client}. A matching backend release has not been published yet. Check for backend updates after the release build finishes; reinstalling the current release will not help.", "protocol_older");
        if (record.ProtocolVersion > client)
            throw Failure($"The latest backend ({record.Version}) requires protocol {record.ProtocolVersion}, but this Assembly uses {client}. Update Assembly before installing this backend.", "protocol_newer");
    }

    internal static bool Hex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

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
