using System.Buffers.Binary;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Persistence;

internal enum TurnReplyOrigin { Live = 0, Recovery = 1 }
internal sealed record TurnReplyReceiptCarrier(TurnReplyOrigin Origin, long TerminalSeq, string ChatRunIdSha256, string RawJournalFingerprint);

internal static class TurnReplyFingerprint
{
    private const string Prefix = "turn-reply-fp/v1/";
    internal static string Encode(string machine, string child, string messageId, TurnReplyReceiptCarrier carrier)
    {
        if (!Enum.IsDefined(carrier.Origin) || carrier.TerminalSeq <= 0 || !RelayHash.IsSha(carrier.ChatRunIdSha256)
            || !RelayHash.IsSha(carrier.RawJournalFingerprint)) throw new ArgumentException("Invalid turn reply carrier.");
        var bytes = new byte[106]; bytes[0] = 1; bytes[1] = (byte)carrier.Origin;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(2, 8), (ulong)carrier.TerminalSeq);
        Convert.FromHexString(carrier.ChatRunIdSha256).CopyTo(bytes, 10);
        Convert.FromHexString(carrier.RawJournalFingerprint).CopyTo(bytes, 42);
        Convert.FromHexString(Integrity(machine, child, messageId, carrier)).CopyTo(bytes, 74);
        return Prefix + Base64(bytes);
    }
    internal static bool TryDecode(string stored, string machine, string child, string messageId, out TurnReplyReceiptCarrier? carrier)
    {
        carrier = null;
        if (stored.Length != 159 || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var text = stored[Prefix.Length..];
        if (text.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))) return false;
        try
        {
            var bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + "==");
            if (bytes.Length != 106 || Base64(bytes) != text || bytes[0] != 1 || bytes[1] > 1) return false;
            var seq = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(2, 8));
            if (seq is 0 or > long.MaxValue) return false;
            var value = new TurnReplyReceiptCarrier((TurnReplyOrigin)bytes[1], (long)seq,
                Convert.ToHexStringLower(bytes.AsSpan(10, 32)), Convert.ToHexStringLower(bytes.AsSpan(42, 32)));
            if (Integrity(machine, child, messageId, value) != Convert.ToHexStringLower(bytes.AsSpan(74, 32))) return false;
            carrier = value; return true;
        }
        catch (Exception error) when (error is FormatException or ArgumentException) { return false; }
    }
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Integrity(string machine, string child, string messageId, TurnReplyReceiptCarrier carrier)
    {
        using var hash = new RelayHash();
        hash.String("turn-reply-fingerprint-carrier/v1"); hash.String(machine); hash.String(child); hash.String(messageId);
        hash.Flag(carrier.Origin == TurnReplyOrigin.Recovery); hash.Length(carrier.TerminalSeq);
        hash.Bytes(Convert.FromHexString(carrier.ChatRunIdSha256)); hash.Bytes(Convert.FromHexString(carrier.RawJournalFingerprint));
        return hash.Finish();
    }
}
