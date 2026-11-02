using Assembly.Engine.Messages;
using Assembly.Engine.Persistence;

namespace Assembly.Engine.Relays;

public sealed record RelayTurnReplyEvidence(string ChatRunId, long TerminalSeq, string ReceiptFingerprint);

internal static class RelayTurnReplies
{
    internal static string Identity(string original, RelayTurnReplyEvidence? evidence) => Bind("relay-turn-reply-identity/v1", original, evidence);
    internal static string Descriptor(string original, RelayTurnReplyEvidence? evidence) => Bind("relay-turn-reply-descriptor/v1", original, evidence);
    private static string Bind(string domain, string original, RelayTurnReplyEvidence? evidence)
    {
        if (evidence is null) return original;
        using var hash = new RelayHash();
        hash.String(domain); hash.String(original); hash.String(evidence.ChatRunId);
        hash.Length(evidence.TerminalSeq); hash.String(evidence.ReceiptFingerprint);
        return hash.Finish();
    }

    internal static void Validate(string machine, string message, string? child, string kind, string mode, RelayTurnReplyEvidence? evidence)
    {
        if (evidence is null)
        {
            if (message.StartsWith("turn-reply/", StringComparison.Ordinal)) throw new RelayRefused("source_changed");
            return;
        }
        if (child is null || kind != "session" || mode != "prompt" || evidence.ChatRunId is null || evidence.TerminalSeq <= 0 ||
            evidence.ReceiptFingerprint is null || message != PreparedTurnReply.MessageId(machine, child, evidence.ChatRunId) ||
            !TurnReplyFingerprint.TryDecode(evidence.ReceiptFingerprint, machine, child, message, out var carrier) ||
            carrier!.TerminalSeq != evidence.TerminalSeq || carrier.ChatRunIdSha256 != RelayHash.Sha(RelayHash.Utf8.GetBytes(evidence.ChatRunId)))
            throw new RelayRefused("source_changed");
    }
}
