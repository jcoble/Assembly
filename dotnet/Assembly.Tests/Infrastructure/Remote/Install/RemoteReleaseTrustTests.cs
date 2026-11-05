using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Remote.Install;

namespace Assembly.Tests.Infrastructure.Remote;

public sealed class RemoteReleaseTrustTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Infrastructure/Remote/Install/Fixtures", name));

    [Fact]
    public void Accepts_a_real_signature_the_old_pipeline_published_with_the_pinned_key()
    {
        RemoteReleaseTrust.Verify(Fixture("old-release.download.json"), Fixture("old-release.download.json.sig"));
    }

    [Fact]
    public void Refuses_changed_bytes()
    {
        var data = Fixture("old-release.download.json");
        data[20] ^= 1;
        var failed = Assert.Throws<CallFailed>(() => RemoteReleaseTrust.Verify(data, Fixture("old-release.download.json.sig")));
        Assert.Equal("release_untrusted", failed.Reason);
    }

    [Fact]
    public void Refuses_another_key()
    {
        var signer = new ReleaseSigner();
        var failed = Assert.Throws<CallFailed>(() => RemoteReleaseTrust.Verify(Fixture("old-release.download.json"), Fixture("old-release.download.json.sig"), signer.PublicKey));
        Assert.Equal("release_untrusted", failed.Reason);
    }

    [Fact]
    public void Refuses_a_changed_trusted_comment()
    {
        var signer = new ReleaseSigner();
        var data = Encoding.UTF8.GetBytes("record");
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(signer.Sign(data))));
        var changed = Convert.ToBase64String(Encoding.UTF8.GetBytes(text.Replace("trusted comment: file:x", "trusted comment: file:y", StringComparison.Ordinal)));
        RemoteReleaseTrust.Verify(data, signer.Sign(data), signer.PublicKey);
        Assert.Throws<CallFailed>(() => RemoteReleaseTrust.Verify(data, Encoding.UTF8.GetBytes(changed), signer.PublicKey));
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("")]
    public void Refuses_a_malformed_signature(string signature)
    {
        Assert.Throws<CallFailed>(() => RemoteReleaseTrust.Verify(Fixture("old-release.download.json"), Encoding.UTF8.GetBytes(signature)));
    }

    // The app's archive is checked as it downloads, so nothing reads the whole of it into memory.
    [Fact]
    public void A_signature_checked_piece_by_piece_as_it_arrives_answers_the_same()
    {
        var signer = new ReleaseSigner();
        var data = new byte[200_000];
        Random.Shared.NextBytes(data);
        var signature = signer.Sign(data);

        var whole = new ReleaseDigest();
        foreach (var piece in data.Chunk(65_536)) whole.Add(piece);
        RemoteReleaseTrust.Verify(whole, (ReadOnlySpan<byte>)signature, signer.PublicKey);

        var changed = new ReleaseDigest();
        foreach (var piece in data.Chunk(65_536)) changed.Add(piece);
        changed.Add([0]);
        Assert.Equal("release_untrusted", Assert.Throws<CallFailed>(() => RemoteReleaseTrust.Verify(changed, (ReadOnlySpan<byte>)signature, signer.PublicKey)).Reason);
    }
}
