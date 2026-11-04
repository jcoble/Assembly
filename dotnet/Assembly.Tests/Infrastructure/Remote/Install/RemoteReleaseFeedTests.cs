using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Remote.Install;
using Assembly.Infrastructure.Socket;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class RemoteReleaseFeedTests
{
    private const string Version = "1.2.3";
    private const string Archive = "assembly-remote-backend-1.2.3-x86_64-unknown-linux-gnu.tar.gz";
    private const string Base = "https://github.com/jcoble/Assembly/releases/download/assembly-v1.2.3/";

    internal sealed class Handler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        internal readonly List<string> Asked = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Asked) Asked.Add(url);
            return Task.FromResult(responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    internal static byte[] Record(int protocol = SocketConnection.Protocol, long bytes = 1000, string version = Version) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version,
            protocolVersion = protocol,
            target = RemoteReleaseFeed.Target,
            archiveFile = Archive,
            bytes,
            sha256 = new string('a', 64)
        }));

    internal const string AdapterBase = "https://github.com/jcoble/Assembly/releases/download/assembly-providers-7-1/";
    internal const string AdapterManifest = "assembly-adapters-linux-x64.json";

    private static object AdapterAsset(string tag, string name, long size) =>
        new { name, size, browser_download_url = $"https://github.com/jcoble/Assembly/releases/download/{tag}/{name}" };

    internal static byte[] Listing(long archiveBytes, int recordBytes, int signatureBytes, string assetHost = "github.com", int adapterManifestBytes = 100, int adapterSignatureBytes = 100) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[]
        {
            // Newer, but unsigned: it never qualifies.
            new { tag_name = "assembly-providers-8-1", draft = false, prerelease = false, published_at = "2026-11-02T00:00:00Z", assets = new object[]
            { AdapterAsset("assembly-providers-8-1", "assembly-adapters-linux-x64.tar.gz", 1000), AdapterAsset("assembly-providers-8-1", AdapterManifest, 100) } },
            new { tag_name = "assembly-providers-7-1", draft = false, prerelease = false, published_at = "2026-11-01T00:00:00Z", assets = new object[]
            {
                AdapterAsset("assembly-providers-7-1", "assembly-adapters-linux-x64.tar.gz", 1000),
                AdapterAsset("assembly-providers-7-1", AdapterManifest, adapterManifestBytes),
                AdapterAsset("assembly-providers-7-1", AdapterManifest + ".sig", adapterSignatureBytes),
            } },
            new { tag_name = "assembly-v9.9.9", draft = true, prerelease = false, assets = Array.Empty<object>() },
            new { tag_name = "assembly-v9.9.8", draft = false, prerelease = true, assets = Array.Empty<object>() },
            new { tag_name = "assembly-v1.2.3", draft = false, prerelease = false, assets = new object[]
            {
                new { name = Archive, size = archiveBytes, browser_download_url = $"https://{assetHost}/jcoble/Assembly/releases/download/assembly-v1.2.3/{Archive}" },
                new { name = Archive + ".download.json", size = recordBytes, browser_download_url = Base + Archive + ".download.json" },
                new { name = Archive + ".download.json.sig", size = signatureBytes, browser_download_url = Base + Archive + ".download.json.sig" },
            } },
        }));

    private static readonly byte[] Manifest = "{\"schemaVersion\":1}"u8.ToArray();

    private static (RemoteReleaseFeed Feed, Handler Handler) Feed(byte[] record, byte[]? signature = null, string assetHost = "github.com", long archiveBytes = 1000, byte[]? adapterSignature = null)
    {
        var signer = new ReleaseSigner();
        var signed = signature ?? signer.Sign(record);
        var adapterSigned = adapterSignature ?? signer.Sign(Manifest);
        var handler = new Handler(new()
        {
            ["https://api.github.com/repos/jcoble/Assembly/releases?per_page=100"] = Listing(archiveBytes, record.Length, signed.Length, assetHost, Manifest.Length, adapterSigned.Length),
            [Base + Archive + ".download.json"] = record,
            [Base + Archive + ".download.json.sig"] = signed,
            [AdapterBase + AdapterManifest] = Manifest,
            [AdapterBase + AdapterManifest + ".sig"] = adapterSigned,
        });
        return (new RemoteReleaseFeed(new HttpClient(handler), signer.PublicKey), handler);
    }

    [Fact]
    public async Task Picks_the_newest_published_backend_and_reads_only_its_small_signed_files()
    {
        var (feed, handler) = Feed(Record());
        var release = await feed.LatestAsync(default);
        Assert.Equal(Base + Archive, release.ArchiveUrl);
        Assert.Equal(Version, release.Record.Version);
        Assert.DoesNotContain(Base + Archive, handler.Asked);
    }

    [Fact]
    public async Task Names_the_newest_signed_adapters_and_the_hash_of_their_manifest()
    {
        var (feed, handler) = Feed(Record());
        var release = await feed.LatestAsync(default);
        Assert.Equal("assembly-providers-7-1", release.Adapters.Tag);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Manifest)), release.Adapters.ManifestSha256);
        Assert.DoesNotContain(handler.Asked, url => url.EndsWith(".tar.gz", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_adapter_manifest_signed_by_another_key_never_becomes_a_release()
    {
        var (feed, _) = Feed(Record(), adapterSignature: new ReleaseSigner().Sign(Manifest));
        Assert.Equal("release_untrusted", (await Assert.ThrowsAsync<CallFailed>(() => feed.LatestAsync(default))).Reason);
    }

    [Fact]
    public async Task A_tampered_record_never_becomes_a_release()
    {
        var original = Record();
        var signature = new ReleaseSigner().Sign(original);
        var (feed, _) = Feed(Record(bytes: 2000), signature);
        Assert.Equal("release_untrusted", (await Assert.ThrowsAsync<CallFailed>(() => feed.LatestAsync(default))).Reason);
    }

    [Theory]
    [InlineData(-1, "protocol_older", "A matching backend release has not been published yet")]
    [InlineData(1, "protocol_newer", "Update Assembly")]
    public async Task Names_both_protocols_when_the_release_does_not_match(int offset, string reason, string message)
    {
        var (feed, _) = Feed(Record(protocol: SocketConnection.Protocol + offset));
        var failed = await Assert.ThrowsAsync<CallFailed>(() => feed.LatestAsync(default));
        Assert.Equal(reason, failed.Reason);
        Assert.Contains(message, failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_an_archive_outside_this_repository()
    {
        var (feed, _) = Feed(Record(), assetHost: "example.com");
        Assert.Equal("release_invalid", (await Assert.ThrowsAsync<CallFailed>(() => feed.LatestAsync(default))).Reason);
    }

    [Fact]
    public async Task Refuses_a_record_that_disagrees_with_the_published_archive()
    {
        var (feed, _) = Feed(Record(bytes: 1000), archiveBytes: 999);
        Assert.Equal("release_invalid", (await Assert.ThrowsAsync<CallFailed>(() => feed.LatestAsync(default))).Reason);
        var (other, _) = Feed(Record(version: "1.2.4"));
        Assert.Equal("release_invalid", (await Assert.ThrowsAsync<CallFailed>(() => other.LatestAsync(default))).Reason);
    }

    [Fact]
    public async Task Says_when_nothing_is_published()
    {
        var feed = new RemoteReleaseFeed(new HttpClient(new Handler(new()
        { ["https://api.github.com/repos/jcoble/Assembly/releases?per_page=100"] = "[]"u8.ToArray() })));
        Assert.Equal("release_missing", (await Assert.ThrowsAsync<CallFailed>(() => feed.LatestAsync(default))).Reason);
    }
}
