using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Engine.Contracts;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Persistence;
using Assembly.Engine.Providers;

namespace Assembly.Engine.Messages;

internal static partial class AuthTransitions
{
    internal static CommandContext ReportContext(string machine, long now, AuthTerminalProof proof) =>
        new(proof.ObservationId, machine, new(ActorKind.System, "adapter"), "auth-terminal/" + proof.ObservationId.ToString("N"), 0, proof.Epoch, now);

    internal static CommandResult Report(IWriteScope scope, CommandContext context, ReportSignInNeeded command,
        Func<string, string, bool>? configured, MessageExpiryPolicy expiryPolicy)
    {
        var proof = command.Proof;
        if (context != ReportContext(context.Machine, context.NowMs, proof) ||
            !AuthTerminalRow.Matches(scope, command.SessionId, command.TerminalSeq, proof)) return Rejected(context);
        var execution = AuthExecution.Current(scope, context, command.SessionId, proof, configured) ??
            AuthExecution.CompletedPositive(scope, context, command.SessionId, proof, configured);
        if (execution is null) return Rejected(context);
        var binding = proof with { ProviderMessageId = null };
        var blockContext = new CommandContext(binding.BlockCommandId, context.Machine, new(ActorKind.System, "adapter"),
            "auth-blocker/" + binding.BlockCommandId.ToString("N"), 0, binding.Epoch, context.NowMs);
        var block = new BlockSignInNeeded(command.SessionId, binding);
        var result = EngineJournal.Execute(scope, blockContext, JsonSerializer.Serialize<EngineCommand>(block, EngineJson.Default.EngineCommand),
            () => Block(scope, blockContext, block, configured, expiryPolicy));
        if (!result.Applied) return result;
        List<EventDraft> events = [];
        if (execution.ConfirmedProviderId is not null)
        {
            events.AddRange(Notice(scope, context, command.SessionId, proof));
            events.Add(MessageTransitions.ApplyInsertedAuthTerminal(scope, context, execution));
        }
        return EngineJournal.Commit(scope, context, result.Version, context.Epoch, events, []);
    }

    internal static CommandResult Block(IWriteScope scope, CommandContext context, BlockSignInNeeded command,
        Func<string, string, bool>? configured, MessageExpiryPolicy expiryPolicy)
    {
        var proof = command.Binding;
        if (context.Actor is not { Kind: ActorKind.System, Id: "adapter" } ||
            context.CommandId != proof.BlockCommandId || context.EntityId != "auth-blocker/" + proof.BlockCommandId.ToString("N") ||
            context.ExpectedVersion != 0 || proof.ProviderMessageId is not null) return Rejected(context);
        var execution = AuthExecution.Current(scope, context, command.SessionId, proof, configured) ??
            AuthExecution.CompletedPositive(scope, context, command.SessionId, proof, configured);
        if (execution is null) return Rejected(context);
        if (execution.User.AuthBlocker is { } retained)
        {
            if (retained.EffectId != execution.Effect.Id || retained.OriginalClaimAttempt != execution.Effect.Attempt ||
                retained.ObservationId != proof.ObservationId || retained.BlockCommandId != proof.BlockCommandId ||
                AuthNeed.Open(scope, context.Machine, retained.NeedId) is not { } existing ||
                existing.SessionId != command.SessionId || existing.Target != execution.Target || existing.Version < retained.NeedVersionAtBlock)
                return Rejected(context);
            return new(true, false, null, existing.Version, 0, context.Epoch);
        }
        var text = AuthNeed.Copy(execution.Target.Provider, context.Machine);
        var current = scope.Query("""
            SELECT id FROM engine_needs_me WHERE machine=$machine AND session_id=$session AND state='open'
              AND kind='confirmation' AND provider_request_id IS NULL AND chat_row_id IS NULL AND (CAST(text AS BLOB)=$text OR CAST(text AS BLOB)=$inserted)
              AND json_type(options_json)='array' AND json_array_length(options_json)=2
              AND json_extract(options_json,'$[0]')='sign_in' AND json_extract(options_json,'$[1]')='dismiss'
            ORDER BY created_ms,id LIMIT 2
            """, r => r.Text(0), new SqlArg("$machine", context.Machine), new("$session", command.SessionId),
            new("$text", System.Text.Encoding.UTF8.GetBytes(text)), new("$inserted", System.Text.Encoding.UTF8.GetBytes(AuthNeed.InsertionCopy(execution.Target.Provider, context.Machine))));
        if (current.Count > 1) return Rejected(context);
        AuthNeed? need = current.Count == 1 ? AuthNeed.Open(scope, context.Machine, current[0]) : null;
        if (current.Count == 1 && need is null) return Rejected(context);
        if (need is null)
        {
            var prefix = "sign-in/" + command.SessionId + "/";
            var number = scope.QueryFirst("""
                SELECT COUNT(*) FROM engine_needs_me WHERE session_id=$session AND substr(id,1,length($prefix))=$prefix
                  AND length(id)>length($prefix) AND substr(id,length($prefix)+1) NOT GLOB '*[^0-9]*'
                """, r => r.Long(0), new SqlArg("$session", command.SessionId), new("$prefix", prefix)) + 1;
            var id = prefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var opened = NeedTransitions.Apply(scope, context with { EntityId = id, ExpectedVersion = 0 },
                new OpenNeed(command.SessionId, execution.Effect.RunId, null, "confirmation", text, ["sign_in", "dismiss"], null, null, null), expiryPolicy);
            if (!opened.Applied) return opened;
            need = AuthNeed.Open(scope, context.Machine, id) ?? throw new InvalidOperationException("The new auth need did not retain its exact target.");
        }
        var provider = ProviderState.Read(scope, execution.Target.Id);
        List<EventDraft> events = [];
        if (provider is null) ProviderState.Create(scope, context, execution.Target, "needs_sign_in", events);
        else
        {
            scope.Execute("""
                UPDATE engine_provider SET version=version+1,auth_state='needs_sign_in',reason=NULL,updated_ms=$now,
                    facts_json=CASE WHEN facts_json IS NULL THEN NULL ELSE json_set(facts_json,'$.auth','needsSignIn') END WHERE id=$id
                """, new("$now", context.NowMs), new("$id", provider.Id));
            events.Add(ProviderState.Changed(provider.Id, provider.Version + 1, "needs_sign_in"));
        }
        var original = proof.Delivery!;
        RowStore.BindAuthBlocker(scope, command.SessionId, original.ClientId, original.UserRowSeq, original.UserRunId,
            new(execution.Effect.Id, execution.Effect.Attempt, proof.ObservationId, need.Id, need.Version, proof.BlockCommandId));
        return EngineJournal.Commit(scope, context, need.Version, context.Epoch, events, []);
    }

    private static CommandResult Rejected(CommandContext context) => new(false, false, "bad_args", 0, 0, context.Epoch);
}
