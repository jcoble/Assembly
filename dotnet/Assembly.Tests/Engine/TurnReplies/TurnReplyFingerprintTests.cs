using Assembly.Engine.Persistence;

namespace Assembly.Tests.Engine.TurnReplies;

public sealed class TurnReplyFingerprintTests
{
    private const string Message = "turn-reply/2b48e069c5bc1cc6c0cada1aca01cd17de3c9b304dbd74d8f8286d24e5168d71";
    private const string RunHash = "7056d6997e3ac641e3e4ba5b4a97f765c3d5a809f619357e85bc205443eb7741";
    private const string Live = "turn-reply-fp/v1/AQAAAAAAAAAAB3BW1pl-OsZB4-S6W0qX92XD1agJ9hk1foW8IFRD63dBEREREREREREREREREREREREREREREREREREREREREREZgkCvInbTydQnF8_sbTjVN_KVATijqUvrucjJwKcH1Q";
    private const string Recovery = "turn-reply-fp/v1/AQEAAAAAAAAAB3BW1pl-OsZB4-S6W0qX92XD1agJ9hk1foW8IFRD63dBERERERERERERERERERERERERERERERERERERERERERFLAZnKi7ECtDfv62aN2ouAxQhprcXuD37JE1WvNTwOAQ";
    [Theory]
    [InlineData(0, Live)]
    [InlineData(1, Recovery)]
    public void Carrier_has_exact_frozen_159_byte_vectors_and_roundtrips(int origin, string expected)
    {
        var material = new TurnReplyReceiptCarrier((TurnReplyOrigin)origin, 7, RunHash, new string('1', 64));
        Assert.Equal(159, expected.Length);
        Assert.Equal(expected, TurnReplyFingerprint.Encode("machine-A", "child-1", Message, material));
        Assert.True(TurnReplyFingerprint.TryDecode(expected, "machine-A", "child-1", Message, out var actual)); Assert.Equal(material, actual);
        foreach (var (machine, child, message) in new[] { ("wrong", "child-1", Message), ("machine-A", "wrong", Message), ("machine-A", "child-1", Message + "0") })
            Assert.False(TurnReplyFingerprint.TryDecode(expected, machine, child, message, out _));
    }

    [Fact]
    public void Deterministic_reply_identity_matches_frozen_vector_and_never_uses_provider_or_workflow_ids()
    {
        var source = new Assembly.Application.Features.Chat.Rows.FinalizedChatReply("child-1", "chat-run-1", 7, "finished", 1000, null, "", null, null);
        var prepared = Assembly.Engine.EngineCommands.PrepareTurnReply(new("child-1", null, null, null, 0),
            new("chat-run-1", "acp", "provider", false, 1000), source);
        Assert.Equal(Message, prepared.MessageId("machine-A"));
        var other = Assembly.Engine.EngineCommands.PrepareTurnReply(new("child-1", "workflow", "step", 2, 9),
            new("chat-run-1", "other-acp", "other-provider", true, 1000), source);
        Assert.Equal(Message, other.MessageId("machine-A"));
        Assert.NotEqual(Message, other.MessageId("other-machine"));
    }

    [Fact]
    public void Every_carrier_bit_and_noncanonical_text_is_rejected_without_throwing()
    {
        var binary = Convert.FromBase64String(Live[17..].Replace('-', '+').Replace('_', '/') + "==");
        for (var at = 0; at < binary.Length; at++)
        {
            var changed = binary.ToArray(); changed[at] ^= 1;
            var text = "turn-reply-fp/v1/" + Convert.ToBase64String(changed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.False(TurnReplyFingerprint.TryDecode(text, "machine-A", "child-1", Message, out _));
        }
        foreach (var value in new[] { "", "garbage", Live + "=", " " + Live, Live + "\n", Live[..^1], Live.Replace('-', '+'), Live[..^1] + "R" })
            Assert.False(TurnReplyFingerprint.TryDecode(value, "machine-A", "child-1", Message, out _));
    }
}
