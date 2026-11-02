using Assembly.Engine.Effects;
using Assembly.Engine.Providers;
using Assembly.Engine.Recording;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Persistence;
using Assembly.Engine.Sessions;
using Assembly.Engine.Runs;
using Assembly.Engine.NeedsMe;
using Assembly.Engine.Messages;
using Assembly.Engine.Handoffs;
using Assembly.Engine.Prs;

namespace Assembly.Engine;

public sealed partial class EngineCommands
{
    private readonly string _machine;
    private readonly Func<string, string, bool>? _isConfiguredProvider;
    private readonly SessionLimits _limits;
    private readonly Func<int> _messageLimit;
    private readonly MessageExpiryPolicy _expiryPolicy;
    private readonly Action<IWriteScope, CommandContext, StartSession>? _projectStart;
    private readonly Func<string, string>? _accountContext;
    private readonly Func<IWriteScope, CommandContext, Effect, EffectReceipt, MessageDelivery?, MessageOutcome?>? _projectCompletion;

    public EngineCommands(string machine, SessionLimits limits, Func<int>? messageLimit = null,
        Action<IWriteScope, CommandContext, StartSession>? projectStart = null, Func<string, string>? accountContext = null,
        Func<IWriteScope, CommandContext, Effect, EffectReceipt, MessageDelivery?, MessageOutcome?>? projectCompletion = null, MessageExpiryPolicy? expiryPolicy = null,
        Func<string, string, bool>? isConfiguredProvider = null)
    {
        _machine = machine;
        _isConfiguredProvider = isConfiguredProvider;
        _projectStart = projectStart;
        _accountContext = accountContext;
        _projectCompletion = projectCompletion;
        _expiryPolicy = expiryPolicy ?? new();
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
        _messageLimit = messageLimit ?? (() => 20);
    }

    public CommandResult Apply(IWriteScope scope, CommandContext context, EngineCommand command, string? preparedError = null)
    {
        if (command is SendOwnerPrompt or RecordProviderCheck)
            return new(false, false, "forbidden", context.ExpectedVersion, 0, context.Epoch);
        // Raw boundary arguments are transient: even the journal fingerprint and size check use structural previews.
        var canonical = RecordingTransitions.JournalJson(command);
        return EngineJournal.Execute(scope, context, canonical, () =>
        {
            if (context.Machine != _machine)
                return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
            if (preparedError is not null)
                return new(false, false, preparedError, context.ExpectedVersion, 0, context.Epoch);
            return command switch
            {
                EffectCommand effect => EffectTransitions.Apply(scope, context, effect, _projectCompletion, _expiryPolicy),
                SignInCommand signIn => SignInTransitions.Apply(scope, context, signIn, _isConfiguredProvider),
                ProviderCommand provider => ProviderTransitions.Apply(scope, context, provider, _isConfiguredProvider),
                RecordingCommand recording => RecordingTransitions.Apply(scope, context, recording),
                PrCommand pr => PrTransitions.Apply(scope, context, pr),
                HandoffCommand handoff => HandoffTransitions.Apply(scope, context, handoff),
                MessageCommand message => MessageTransitions.Apply(scope, context, message, _messageLimit(), _expiryPolicy),
                NeedCommand need => NeedTransitions.Apply(scope, context, need, _expiryPolicy),
                RunCommand run => RunTransitions.Apply(scope, context, run),
                StepCommand step => StepTransitions.Apply(scope, context, step),
                SessionCommand session => ApplySession(scope, context, session),
                _ => new(false, false, "unsupported_command", context.ExpectedVersion, 0, context.Epoch)
            };
        });
    }

    public CommandResult ApplyOwnerPrompt(IWriteScope scope, CommandContext context, PreparedOwnerPrompt prepared)
    {
        var result = EngineJournal.Execute(scope, context, prepared.CanonicalJournal, () =>
        {
            if (context.Machine != _machine || prepared.Machine != _machine)
                return new(false, false, "wrong_machine", context.ExpectedVersion, 0, context.Epoch);
            if (context.Actor.Kind != ActorKind.Window || context.Actor.Id != "owner")
                return new(false, false, "forbidden", context.ExpectedVersion, 0, context.Epoch);
            if (context.ExpectedVersion != 0 || context.Epoch != 0 ||
                context.EntityId != OwnerPromptIdentity.MessageId(prepared.ToSessionId, prepared.ClientId) ||
                context.CommandId != OwnerPromptIdentity.CommandId(_machine, prepared.ToSessionId, prepared.ClientId))
                return new(false, false, "bad_args", context.ExpectedVersion, 0, context.Epoch);
            return MessageTransitions.ApplyOwnerPrompt(scope, context, prepared, _expiryPolicy);
        });
        return result.Error == "command_id_conflict" ? result with { Error = "client_id_conflict" } : result;
    }

    private CommandResult ApplySession(IWriteScope scope, CommandContext context, SessionCommand command, byte[]? titleUtf8 = null, PreparedStartEffectPayload? startEffectPayload = null)
    {
        var depth = _limits.ReadDepth();
        var children = _limits.ReadChildren();
        var accountContext = command is StartSession start ? _accountContext?.Invoke(start.Provider) ?? "" : "";
        var result = SessionTransitions.ApplyPreparedTitle(scope, context, command, depth, children, accountContext, titleUtf8, _expiryPolicy, startEffectPayload);
        if (result.Applied && command is StartSession accepted)
            _projectStart?.Invoke(scope, context, accepted);
        return result;
    }
}
