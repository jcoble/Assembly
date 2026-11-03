using Assembly.Engine.Handoffs;
using Assembly.Engine.Prs;
using System.Text.Json;
using Assembly.Application.Common.Database;
using Assembly.Engine.Hosting;
using Assembly.Engine.Relays;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Messages;
using Assembly.Infrastructure.Engine.Needs;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

public sealed record MachineCommand(Guid CommandId, string FromMachine, string EntityId,
    long ExpectedVersion, long Epoch, EngineCommand Command);
public sealed record StartLocationArgs(string ParentSessionId);
public sealed record StartLocation(string Folder);

/// <summary>The authenticated owner's ordinary machine-coordinator operations. Adoption remains unbound.</summary>
public sealed partial class MachineCalls(EngineRuntime runtime, TimeProvider time, IAgentLauncher launcher, string checkouts, NeedAnswers needs) : IFeature
{
    public void Register(ICallRegistry calls)
    {
        RegisterForeignAncestry(calls);
        RegisterRelay(calls);
        RegisterParentNeeds(calls);
        RegisterForeignTitles(calls);
        RegisterMachineStartAck(calls);
        RegisterHandoffs(calls);
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
        if (args.Command is not (RegisterForeignSession or StartSession or SendMessage or MessageOutcome or ApplyPrObservation or PrObservationRecorded))
            return "forbidden";
        if (args.Command is ApplyPrObservation apply && (args.FromMachine != apply.OriginMachine || args.EntityId != apply.ObservationId) ||
            args.Command is PrObservationRecorded ack && (args.FromMachine != ack.CoordinatorMachine || args.EntityId != ack.ObservationId))
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


    private void RegisterMachineStartAck(ICallRegistry calls)
    {
        RegisterStartTitles(calls);
        Register(false, "start_ack");
        Register(true, "start_ack_bulk");

        void Register(bool bulk, string method) => calls.Write("engine", method,
            EngineSocketJson.Default.MachineStartAckArgs, EngineSocketJson.Default.MachineStartAck,
            (args, call) => Relay<Func<IWriteScope, MachineStartAck>>(call, operation =>
            {
                try
                {
                    var prepared = runtime.Reader.PrepareMachineStartAck(args, operation.Token);
                    return Task.FromResult(operation.Return<Func<IWriteScope, MachineStartAck>>(scope =>
                        runtime.Reader.ReadMachineStartAck(scope, prepared)));
                }
                catch (Exception error) when (error is ArgumentException or JsonException)
                { throw new CallFailed("bad_args"); }
            }), order: args => args.FromMachine, bulk: bulk);
    }

    private void RegisterStartTitles(ICallRegistry calls)
    {
        calls.Write("engine", "start_title_begin", EngineSocketJson.Default.StartTitleBeginArgs, EngineSocketJson.Default.StartTitleBeginReply,
            async (args, call) =>
            {
                var reply = await StartTitles(call, operation =>
                {
                    var request = StartRequest(args); var evidence = StartEvidence(request.Start, operation.Token);
                    var bounded = new StartTitleBeginReply(evidence, evidence.State == MachineStartAckState.Missing ? new string('0', 32) : null);
                    Bounded(call, bounded, EngineSocketJson.Default.StartTitleBeginReply);
                    var handle = evidence.State == MachineStartAckState.Missing ? operation.PublishStartTarget(request) : null;
                    return Task.FromResult(operation.Return(new StartTitleBeginReply(evidence, handle)));
                });
                return _ => reply;
            }, order: args => args.Start.FromMachine, bulk: true);
        calls.Write("engine", "start_title_part", EngineSocketJson.Default.StartTitlePartArgs, EngineSocketJson.Default.StartTitleOffsets,
            async (args, call) =>
            {
                var reply = await StartTitles(call, operation => operation.AppendStartTitle(args)); return _ => reply;
            }, bulk: true);
        calls.Write("engine", "start_title_drop", EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean,
            async (args, call) =>
            {
                var reply = await StartTitles(call, operation => Task.FromResult(operation.DropStartTarget(args.Handle))); return _ => reply;
            }, bulk: true);
        calls.Write("engine", "start_title_commit", EngineSocketJson.Default.StartTitleCommitArgs, EngineJson.Default.CommandResult,
            (args, call) => StartTitles<Func<IWriteScope, CommandResult>>(call, async operation =>
            {
                var request = StartRequest(args.Request);
                var snapshot = runtime.Reader.PrepareMachineStartAck(request.Start, operation.Token);
                var evidence = ((IEngineRuntime)runtime).Read(scope => runtime.Reader.ReadMachineStartAck(scope, snapshot), operation.Token);
                if (evidence.State is MachineStartAckState.Accepted or MachineStartAckState.Refused)
                    return operation.Return<Func<IWriteScope, CommandResult>>(scope =>
                    {
                        var current = runtime.Reader.ReadMachineStartAck(scope, snapshot);
                        return current.State is MachineStartAckState.Accepted or MachineStartAckState.Refused && current.Recorded is { } recorded
                            ? recorded : throw new CallFailed("Start outcome is unknown", "unknown");
                    });
                if (evidence.State != MachineStartAckState.Missing) throw new CallFailed("Start outcome is unknown", "unknown");
                PreparedSessionTitleCommand? prepared = await operation.AdmitStartTitle(args with { Request = request }, runtime.Machine);
                var query = request.Start;
                var error = Prepare(new(query.CommandId, query.FromMachine, query.EntityId, query.ExpectedVersion, query.Epoch, prepared.Command));
                return scope =>
                {
                    try
                    {
                        if (scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0)) != runtime.Machine)
                            throw new CallFailed("wrong_machine");
                        var context = new CommandContext(query.CommandId, runtime.Machine, new(ActorKind.System, "machine:" + query.FromMachine),
                            query.EntityId, query.ExpectedVersion, query.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
                        return runtime.Commands.ApplyPreparedSessionTitle(scope, context, prepared!, error);
                    }
                    finally { prepared = null; }
                };
            }), order: args => args.Request.Start.FromMachine, bulk: true);
    }
    private StartTitleBeginArgs StartRequest(StartTitleBeginArgs args)
    {
        var frozen = StartTitlePayload.Freeze(args);
        if (frozen.TargetMachine != runtime.Machine) throw new CallFailed("wrong_machine");
        return frozen;
    }
    private MachineStartAck StartEvidence(MachineStartAckArgs query, CancellationToken ct)
    {
        var prepared = runtime.Reader.PrepareMachineStartAck(query, ct);
        return ((IEngineRuntime)runtime).Read(scope => runtime.Reader.ReadMachineStartAck(scope, prepared), ct);
    }
    private Task<T> StartTitles<T>(CallContext call, Func<Relays.RelayMessageParts.Operation, Task<T>> body) => Relay(call, async operation =>
    {
        try { return await body(operation).ConfigureAwait(false); }
        catch (Exception error) when (error is ArgumentException or JsonException) { throw new CallFailed("bad_parts"); }
    });

    private void RegisterHandoffs(ICallRegistry calls)
    {
        calls.Read("engine", "handoff_ack", EngineSocketJson.Default.HandoffIdentity, EngineSocketJson.Default.HandoffAck,
            (args, call) => Relay(call, operation => Task.FromResult(operation.Return(Bounded(call,
                ((IEngineRuntime)runtime).Read(scope =>
                {
                    RequireHandoffReceiver(scope, args.SourceMachine, args.TargetMachine);
                    return runtime.Commands.ReadHandoffAck(scope, args);
                }, operation.Token), EngineSocketJson.Default.HandoffAck)))));

        calls.Write("engine", "handoff_phase", EngineSocketJson.Default.HandoffPhaseArgs, EngineSocketJson.Default.HandoffAck,
            (args, call) => Relay<Func<IWriteScope, HandoffAck>>(call, operation =>
                Task.FromResult(operation.Return<Func<IWriteScope, HandoffAck>>(scope =>
                {
                    RequireHandoffReceiver(scope, args.Identity.SourceMachine);
                    return Bounded(call, runtime.Commands.ApplyHandoffPhase(scope, args, time.GetUtcNow().ToUnixTimeMilliseconds()),
                        EngineSocketJson.Default.HandoffAck);
                }))), order: args => args.Identity.HandoffId);

        calls.Write("engine", "handoff_adopt", EngineSocketJson.Default.HandoffAdoptArgs, EngineSocketJson.Default.HandoffAck,
            (args, call) => Relay<Func<IWriteScope, HandoffAck>>(call, operation =>
                Task.FromResult(operation.Return<Func<IWriteScope, HandoffAck>>(scope =>
                {
                    RequireHandoffReceiver(scope, args.Identity.TargetMachine);
                    return Bounded(call, runtime.Commands.ApplyHandoffAdopt(scope, args, time.GetUtcNow().ToUnixTimeMilliseconds()),
                        EngineSocketJson.Default.HandoffAck);
                }))), order: args => args.Identity.HandoffId);

        calls.Write("engine", "handoff_seal", EngineSocketJson.Default.HandoffSealArgs, EngineSocketJson.Default.HandoffAck,
            (args, call) => Relay<Func<IWriteScope, HandoffAck>>(call, operation =>
                Task.FromResult(operation.Return<Func<IWriteScope, HandoffAck>>(scope =>
                {
                    RequireHandoffReceiver(scope, args.Identity.TargetMachine);
                    return Bounded(call, runtime.Commands.SealHandoffTarget(scope, args, time.GetUtcNow().ToUnixTimeMilliseconds()),
                        EngineSocketJson.Default.HandoffAck);
                }))), order: args => args.Identity.HandoffId);
    }

    // Routing and the current physical receiver are checked in the supplied snapshot/writer. The Engine owns
    // identity, epoch, readiness and immutable receipt admission; this door grants no additional authority.
    private void RequireHandoffReceiver(IReadScope scope, string machine, string? alternative = null)
    {
        if (runtime.Machine != machine && runtime.Machine != alternative ||
            scope.QueryFirst("SELECT machine_id FROM engine_meta WHERE singleton=1", row => row.Text(0)) != runtime.Machine)
            throw new CallFailed("wrong_machine");
    }
}
