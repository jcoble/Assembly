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
    public void Independent_binary_vectors_pin_new_digest_and_existing_context_encodings()
    {
        var capture = RelayDigests.Capture("source", "target", "message", "session", new("sender", "recipient", "Hello", "prompt"),
            "Captured", new(1, "queued", 0, false, 0, "local", null, 0, "target"));
        var descriptor = RelayDigests.Describe(capture);
        Assert.Equal("ef03753ff608cd275b486a4001795f3b179438bfd5ad0d43c42a44071bdf7173", capture.IdentitySha256);
        Assert.Equal("80cb8deabda8ca02d141c86f1b153651eb02cb41c709f4aa664dfad81221a076", capture.SourceSha256);
        Assert.Equal("d9a6ec2543bb660598cb9bd88572dcc5cd45d0ad5870690f0eb3dc7b6711e857", descriptor.CommandSha256);
        Assert.Equal("cdd1ac0dccba33e62788bad4c4f904ac217b1e2306efcd06f9e8e50e4965fb53", RelayDigests.DescriptorCommitment(descriptor));
        var key = RelayReceiptKeys.ForeignParent("target", "node");
        Assert.Equal("foreign-parent/v1/609c86fb29f3da4355d4f0f7d545a2ff39e80bf25cdbc1f6bf204fcc525d75e7", key);
        Assert.Equal(Guid.Parse("b9584354-2a95-67fd-64e4-5f31adceda0f"), RelayReceiptKeys.ContextGuid(key));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(40000)]
    [InlineData(15000)]
    public void Canonical_stream_matches_complete_generated_bytes_and_old_small_branch(int length)
    {
        var text = new string('a', 511) + "😀" + new string(length == 15000 ? '界' : 'x', length) + "\0\"\nAFTER_NUL";
        var command = new SendMessage("sender", "recipient", text, "prompt");
        var generated = JsonSerializer.SerializeToUtf8Bytes<EngineCommand>(command, EngineJson.Default.EngineCommand);
        var prepared = PreparedTextJson.Create(command with { Text = "" }, "text", text);
        Assert.Equal(generated.LongLength, prepared.Utf8Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(generated)), prepared.Sha256);
        using var visited = new MemoryStream();
        PreparedTextJson.VisitCanonical(command with { Text = "" }, "text", text, bytes => visited.Write(bytes));
        Assert.Equal(generated, visited.ToArray());
        Assert.Equal(generated.Length <= 32768 ? Encoding.UTF8.GetString(generated) : null, prepared.SmallCanonical);
    }

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
