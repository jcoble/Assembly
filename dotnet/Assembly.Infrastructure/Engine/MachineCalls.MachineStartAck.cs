using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Sessions;
using Assembly.Engine;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Relays;

namespace Assembly.Infrastructure.Engine;

public sealed partial class MachineCalls
{
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

}
