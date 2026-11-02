using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Engine.Persistence;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Messages;

/// <summary>Frozen terminal material, not a wire command or an authorization capability.</summary>
public sealed class PreparedTurnReply
{
    internal SessionObservation? Observed { get; }
    internal SessionTurnReply? Final { get; }
    internal RecoveredChatTurn? Recovered { get; }
    internal FinalizedChatReply Source { get; }
    internal string? RecoveryMachine { get; }
    internal long Epoch { get; }
    internal byte[] Body { get; }
    internal TurnReplyOrigin Origin => Recovered is null ? TurnReplyOrigin.Live : TurnReplyOrigin.Recovery;
    private PreparedTurnReply(SessionObservation? observed, SessionTurnReply? final, RecoveredChatTurn? recovered,
        FinalizedChatReply source, string? recoveryMachine, long epoch)
    {
        Observed = observed; Final = final; Recovered = recovered; Source = source; RecoveryMachine = recoveryMachine; Epoch = epoch;
        Body = RelayHash.Utf8.GetBytes(source.Text);
    }
    internal static PreparedTurnReply Live(SessionObservation observed, SessionTurnReply final, FinalizedChatReply source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (observed.SessionId != source.SessionId || final.ChatRunId != source.ChatRunId || final.EndedAt != source.EndedAt
            || observed.Epoch < 0 || source.TerminalSeq <= 0) throw new CallFailed("turn_reply_changed");
        return new(observed, final, null, source, null, observed.Epoch);
    }
    internal static PreparedTurnReply Recovery(RecoveredChatTurn recovered, FinalizedChatReply source, string machine, long epoch)
    {
        if (recovered.SessionId != source.SessionId || recovered.ChatRunId != source.ChatRunId || recovered.EndedAt != source.EndedAt
            || source.TerminalState != "failed" || source.TerminalSeq <= recovered.SavedThrough || recovered.StartedTurnSeq <= 0
            || recovered.SavedThrough < recovered.StartedTurnSeq || epoch < 0 || string.IsNullOrEmpty(machine)) throw new CallFailed("turn_reply_changed");
        return new(null, null, recovered, source, machine, epoch);
    }
    internal string MessageId(string machine) => MessageId(machine, Source.SessionId, Source.ChatRunId);
    internal static string MessageId(string machine, string child, string chatRun)
    {
        using var hash = new RelayHash(); hash.Raw("assembly-turn-reply-id/v1"u8);
        hash.String(machine); hash.String(child); hash.String(chatRun);
        return "turn-reply/" + hash.Finish();
    }
    internal string Canonical(string machine)
    {
        using var hash = new RelayHash();
        hash.String("turn-reply-material/v1"); hash.String(machine); hash.String(Source.SessionId); hash.String(Source.ChatRunId);
        hash.Flag(Origin == TurnReplyOrigin.Recovery); hash.Length(Epoch);
        hash.NullableString(Observed?.RunId); hash.NullableString(Observed?.StepId);
        hash.Flag(Observed?.Attempt is not null); if (Observed?.Attempt is { } attempt) hash.Length(attempt);
        hash.NullableString(Final?.AcpSessionId); hash.NullableString(Final?.ProviderMessageId); hash.Flag(Final?.SelfStarted == true);
        hash.Length(Source.TerminalSeq); hash.String(Source.TerminalState); hash.Length(Source.EndedAt);
        hash.Flag(Source.MessageSeq is not null); if (Source.MessageSeq is { } message) hash.Length(message);
        hash.Length(Body.LongLength); hash.String(RelayHash.Sha(Body));
        hash.Flag(Source.ErrorSeq is not null); if (Source.ErrorSeq is { } error) hash.Length(error);
        hash.NullableString(Source.Error is { } text ? RelayHash.Sha(RelayHash.Utf8.GetBytes(text)) : null);
        if (Source.Error is { } errorText) hash.Length(RelayHash.Utf8.GetByteCount(errorText));
        if (Recovered is { } recovered) { hash.Length(recovered.StartedTurnSeq); hash.Length(recovered.SavedThrough); }
        return "{\"kind\":\"turn_reply\",\"turnReplyJournalVersion\":1,\"materialSha256\":\"" + hash.Finish() + "\"}";
    }
}
