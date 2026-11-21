using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;
using Assembly.Engine.Relays;

namespace Assembly.Tests.Engine.Relays;

public sealed class RelayDigestTests
{
    [Fact]
    public void Descriptor_binds_all_immutable_fields_but_not_current_fence()
    {
        var descriptor = RelayDigests.Describe(RelayDigests.Capture("source", "target", "message", "session",
            new("sender", "recipient", "Hello", "prompt"), "Captured", new(1, "queued", 0, false, 0, "local", null, 0, "target")));
        var original = RelayDigests.DescriptorCommitment(descriptor);
        foreach (var changed in new[] { descriptor with { FromMachine = "source2" }, descriptor with { RecipientMachine = "target2" },
            descriptor with { MessageId = "message2" }, descriptor with { FromSessionId = null }, descriptor with { FromSessionId = "" },
            descriptor with { SenderKind = "system" }, descriptor with { ToSessionId = "recipient2" }, descriptor with { Mode = "interrupt" },
            descriptor with { Body = descriptor.Body with { Utf8Bytes = long.MaxValue } },
            descriptor with { SenderLabel = descriptor.SenderLabel with { Sha256 = new string('0', 64) } },
            descriptor with { CommandUtf8Bytes = descriptor.CommandUtf8Bytes + 1 }, descriptor with { SourceSha256 = new string('0', 64) } })
            Assert.NotEqual(original, RelayDigests.DescriptorCommitment(changed));
        Assert.NotEqual(RelayDigests.DescriptorCommitment(descriptor with { FromSessionId = null }),
            RelayDigests.DescriptorCommitment(descriptor with { FromSessionId = "" }));
        Assert.Equal(original, RelayDigests.DescriptorCommitment(descriptor with { Fence = descriptor.Fence with { MessageVersion = 99, SenderEpoch = 5 } }));
        Assert.Throws<RelayRefused>(() => RelayDigests.DescriptorCommitment(descriptor with { IdentitySha256 = descriptor.IdentitySha256.ToUpperInvariant() }));
        Assert.Throws<RelayRefused>(() => RelayDigests.DescriptorCommitment(descriptor with { Body = descriptor.Body with { Utf8Bytes = -1 } }));
        Assert.True(Encoding.UTF8.GetByteCount(RelayDigests.Canonical(descriptor)) < 1024);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(long.MaxValue)]
    public void Closed_epoch_codec_preserves_exact_original_requested_epoch(long epoch)
    {
        var hash = new string('a', 64);
        var encoded = RelayFingerprint.Encode(epoch, hash);
        Assert.True(RelayFingerprint.TryDecode(encoded, out var decoded, out var actual));
        Assert.Equal(epoch, decoded); Assert.Equal(hash, actual);
        foreach (var invalid in new[] { hash, "relay-send-fp/v2/7/" + hash, "relay-send-fp/v1/+7/" + hash,
            "relay-send-fp/v1/07/" + hash, "relay-send-fp/v1/-1/" + hash, "relay-send-fp/v1/ 7/" + hash,
            "relay-send-fp/v1/9223372036854775808/" + hash, "relay-send-fp/v1/7/" + hash.ToUpperInvariant(),
            "relay-send-fp/v1/7/" + hash + "/extra" })
            Assert.False(RelayFingerprint.TryDecode(invalid, out _, out _));
    }
}
