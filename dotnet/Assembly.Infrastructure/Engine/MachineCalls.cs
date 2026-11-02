using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

public sealed record MachineCommand(Guid CommandId, string FromMachine, string EntityId,
    long ExpectedVersion, long Epoch, EngineCommand Command);
public sealed record StartLocationArgs(string ParentSessionId);
public sealed record StartLocation(string Folder);

/// <summary>The authenticated owner's ordinary machine-coordinator operations. Adoption remains unbound.</summary>
public sealed partial class MachineCalls(EngineRuntime runtime, TimeProvider time, IAgentLauncher launcher, string checkouts) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        RegisterForeignAncestry(calls);
        RegisterRelay(calls);
        RegisterForeignTitles(calls);
        calls.Read("engine", "start_location", EngineSocketJson.Default.StartLocationArgs, EngineSocketJson.Default.StartLocation,
            (args, call) =>
            {
                RequireOwner(call);
                return Task.FromResult(new StartLocation(Folder(args.ParentSessionId)));
            });
        calls.Write("engine", "machine_command", EngineSocketJson.Default.MachineCommand, EngineJson.Default.CommandResult,
            (args, call) =>
            {
                RequireOwner(call);
                if (string.IsNullOrWhiteSpace(args.FromMachine) || args.FromMachine == runtime.Machine)
                    throw new CallFailed("forbidden");
                var error = Prepare(args);
                var title = args.Command is StartSession or RegisterForeignSession
                    ? EngineCommands.PrepareSessionTitle((SessionCommand)args.Command) : null;
                return Task.FromResult<Func<Assembly.Application.Common.Database.IWriteScope, CommandResult>>(scope =>
                {
                    var context = new CommandContext(args.CommandId, runtime.Machine, new(ActorKind.System, "machine:" + args.FromMachine),
                        args.EntityId, args.ExpectedVersion, args.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
                    // Replay precedes prepared refusals and the transition's authoritative same-scope ownership checks.
                    return title is null ? runtime.Commands.Apply(scope, context, args.Command, error)
                        : runtime.Commands.ApplyPreparedSessionTitle(scope, context, title, error);
                });
            }, order: args => args.FromMachine);
    }

    private string? Prepare(MachineCommand args)
    {
        if (args.CommandId == Guid.Empty || args.ExpectedVersion < 0 || args.Epoch < 0 || !EntityId(args.EntityId, args.Command))
            return "bad_args";
        if (args.Command is not (RegisterForeignSession or StartSession or SendMessage or MessageOutcome))
            return "forbidden";
        if (args.Command is not StartSession start) return null;
        if (!launcher.Agents.Any(agent => agent.Id == start.Provider)) return "bad_args";
        if (start.ManagedCheckout == true)
        {
            if (string.IsNullOrWhiteSpace(start.RepoUrl) || start.ParentId is null) return "bad_args";
            try { if (start.Folder != Folder(start.ParentId)) return "bad_args"; }
            catch (CallFailed) { return "bad_args"; }
        }
        else
        {
            if (start.RepoUrl is not null) return "bad_args";
            try { launcher.ValidateFolder(start.Folder); }
            catch (CallFailed) { return "bad_args"; }
        }
        return null;
    }

    private static bool EntityId(string id, EngineCommand command) => Guid.TryParse(id, out _) ||
        command is SendMessage && id.StartsWith("child-ended/", StringComparison.Ordinal) && Guid.TryParse(id[12..], out _);

    private string Folder(string parent) => parent is not null && !parent.Contains('\\') &&
        SessionStore.Folder(checkouts, new SessionInfo(parent, "", "", null, null, 0)) is { } folder
            ? folder : throw new CallFailed("Invalid parent session id");

    private static void RequireOwner(CallContext call)
    {
        if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
    }
}
