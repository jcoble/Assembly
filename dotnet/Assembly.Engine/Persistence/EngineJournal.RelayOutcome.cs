using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Reading;
using Assembly.Engine.Relays;

namespace Assembly.Engine.Persistence;

public static partial class EngineJournal
{
    internal static CommandResult ApplyRelayOutcome(IWriteScope scope, string machine, RelayOutcomeArgs args,
        Func<int> messageLimit, MessageExpiryPolicy policy, long nowMs)
    {
        RelayDigests.Validate(args.Source);
        if (args.Source.FromMachine != machine || args.RecipientMachine != args.Source.RecipientMachine ||
            args.IdentitySha256 != args.Source.IdentitySha256 || args.ExpectedVersion < 0 || args.Epoch < 0)
            throw new RelayRefused("bad_args");
        // The unchanged outcome canonical binds the outcome/fence, so its claimed immutable source must
        // also match the actual owning row on replay. Current mutable fences are checked only for new work.
        var owning = EngineReader.ReadRelaySource(scope, machine, new(args.Source.MessageId, args.RecipientMachine));
        if (RelayDigests.Describe(owning) with { Fence = args.Source.Fence } != args.Source)
            throw new RelayRefused("source_changed");
        var canonical = JsonSerializer.Serialize<EngineCommand>(args.Outcome, EngineJson.Default.EngineCommand);
        using var hash = new RelayHash();
        hash.String("relay-outcome-identity/v1"); hash.String(args.IdentitySha256); hash.String(machine);
        hash.String(args.RecipientMachine); hash.String(args.Source.MessageId); hash.String(canonical);
        hash.Length(args.ExpectedVersion); hash.Length(args.Epoch);
        var key = "relay-outcome/v1/" + hash.Finish();
        var context = new CommandContext(RelayReceiptKeys.ContextGuid(key), machine, new(ActorKind.System, "machine:" + args.RecipientMachine),
            args.Source.MessageId, args.ExpectedVersion, args.Epoch, nowMs);
        var admission = RelayOutcomeAdmission.Create(scope, context, args, messageLimit, policy);
        return ExecuteCore(scope, context, key, canonical, admission.Transition);
    }

    private sealed class RelayOutcomeAdmission
    {
        private readonly IWriteScope _scope;
        private readonly CommandContext _context;
        private readonly RelayOutcomeArgs _args;
        private readonly Func<int> _messageLimit;
        private readonly MessageExpiryPolicy _policy;
        private RelayOutcomeAdmission(IWriteScope scope, CommandContext context, RelayOutcomeArgs args, Func<int> messageLimit, MessageExpiryPolicy policy)
        { _scope = scope; _context = context; _args = args; _messageLimit = messageLimit; _policy = policy; }
        internal static RelayOutcomeAdmission Create(IWriteScope scope, CommandContext context, RelayOutcomeArgs args, Func<int> messageLimit, MessageExpiryPolicy policy) =>
            new(scope, context, args, messageLimit, policy);
        internal CommandResult Transition()
        {
            var actual = EngineReader.ReadRelaySource(_scope, _context.Machine, new(_args.Source.MessageId, _args.RecipientMachine));
            if (RelayDigests.Describe(actual) != _args.Source || !RelayDigests.CanSend(_args.Source) ||
                _args.ExpectedVersion != actual.Fence.MessageVersion || _args.Epoch != actual.Fence.RecipientStubEpoch)
                throw new RelayRefused("source_changed");
            var ack = _args.DestinationAck;
            if (ack is null || ack.Unknown || ack.IdentitySha256 != _args.IdentitySha256 ||
                ack.FirstKey != RootKey(_args.Source) || ack.RetryKey != RetryKey(_args.Source)) throw new RelayRefused("bad_args");
            if (ack.DestinationPresent)
            {
                var evidence = ack.Destination;
                if (evidence is not { Present: true } || evidence.IdentitySha256 != _args.IdentitySha256 ||
                    evidence.SourceSha256 != _args.Source.SourceSha256 || evidence.MessageVersion is null or < 1 ||
                    evidence.State is not ("delivered_to_turn" or "next_turn" or "unsupported" or "failed") ||
                    _args.Outcome != new MessageOutcome(evidence.State, evidence.ProviderTurnId, evidence.Reason))
                    throw new RelayRefused("bad_args");
            }
            else
            {
                var refusal = ack.Retry ?? ack.First;
                if (refusal is not { Applied: false, Error: not null } ||
                    ack.Retry is not null && ack.First is not { Applied: false, Error: "stale_epoch" } ||
                    ack.Retry is null && refusal.Error == "stale_epoch" ||
                    _args.Outcome != new MessageOutcome("failed", null, refusal.Error)) throw new RelayRefused("bad_args");
            }
            return MessageTransitions.ApplyRelayOutcome(_scope, _context, _args.Outcome, _messageLimit(), _policy);
        }
    }
}
