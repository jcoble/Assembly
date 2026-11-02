using Assembly.Infrastructure.Engine.Relays;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine.Contracts;
using Assembly.Engine.Hosting;
using Assembly.Engine.Sessions;

namespace Assembly.Infrastructure.Engine;

public sealed partial class MachineCalls
{
    private void RegisterForeignTitles(ICallRegistry calls)
    {
        calls.Read("engine", "foreign_ancestry_title_ack", EngineSocketJson.Default.ForeignAncestryProofQuery, EngineSocketJson.Default.ForeignTitleReply,
            (args, call) => ForeignTitles(call, op => Task.FromResult(op.Return(Bounded(call,
                ((IEngineRuntime)runtime).Read(s => runtime.Commands.ReadForeignAncestryTitleAck(s, args), op.Token), EngineSocketJson.Default.ForeignTitleReply)))), bulk: true);
        calls.Read("engine", "foreign_register_title_ack", EngineSocketJson.Default.ForeignRegisterReceiptQuery, EngineSocketJson.Default.ForeignTitleReply,
            (args, call) => ForeignTitles(call, op => Task.FromResult(op.Return(Bounded(call,
                ((IEngineRuntime)runtime).Read(s => runtime.Commands.ReadForeignRegisterTitleAck(s, args), op.Token), EngineSocketJson.Default.ForeignTitleReply)))), bulk: true);
        calls.Read("engine", "foreign_title_source_open", EngineSocketJson.Default.ForeignTitleSourceOpenArgs, EngineSocketJson.Default.ForeignTitleSourceOpened,
            (args, call) => ForeignTitles(call, op =>
            {
                var capture = runtime.Reader.ForeignTitleSource(args, op.Token);
                Bounded(call, new ForeignTitleSourceOpened(new string('0', 32), capture.Manifest), EngineSocketJson.Default.ForeignTitleSourceOpened);
                return Task.FromResult(Bounded(call, op.PublishForeignSource(capture), EngineSocketJson.Default.ForeignTitleSourceOpened));
            }), bulk: true);
        calls.Read("engine", "foreign_title_source_part", EngineSocketJson.Default.ForeignTitlePartArgs, EngineSocketJson.Default.ForeignTitlePart,
            (args, call) => ForeignTitles(call, async op => Bounded(call, await op.ReadForeignSource(args), EngineSocketJson.Default.ForeignTitlePart)), bulk: true);
        calls.Read("engine", "foreign_title_source_check", EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.ForeignTitleSourceChecked,
            (args, call) => ForeignTitles(call, op => Task.FromResult(op.Return(Bounded(call,
                runtime.Reader.CheckForeignTitleSource(op.ForeignSourceCapture(args.Handle), op.Token), EngineSocketJson.Default.ForeignTitleSourceChecked)))), bulk: true);
        calls.Read("engine", "foreign_title_source_close", EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean,
            (args, call) => ForeignTitles(call, op => Task.FromResult(op.CloseForeignSource(args.Handle))), bulk: true);
        calls.Write("engine", "foreign_title_begin", EngineSocketJson.Default.ForeignTitleRequest, EngineSocketJson.Default.ForeignTitleReply,
            async (args, call) =>
            {
                var result = await ForeignTitles(call, op =>
                {
                    var request = TitleRequest(args); var probe = ProbeTitle(request, op.Token);
                    Bounded(call, probe with { Handle = probe.Kind == ForeignTitleReplyKind.PartsRequired ? new string('0', 32) : null }, EngineSocketJson.Default.ForeignTitleReply);
                    return Task.FromResult(Bounded(call, op.PublishForeignTarget(request, probe), EngineSocketJson.Default.ForeignTitleReply));
                });
                return _ => result;
            }, order: args => args.FromMachine, bulk: true);
        calls.Write("engine", "foreign_title_part", EngineSocketJson.Default.ForeignTitleAppendArgs, EngineSocketJson.Default.ForeignTitleOffsets,
            async (args, call) =>
            {
                var result = await ForeignTitles(call, op => op.AppendForeignTitle(args)); return _ => result;
            }, bulk: true);
        calls.Write("engine", "foreign_title_drop", EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean,
            async (args, call) =>
            {
                var result = await ForeignTitles(call, op => Task.FromResult(op.DropForeignTarget(args.Handle))); return _ => result;
            }, bulk: true);
        calls.Write("engine", "foreign_title_commit", EngineSocketJson.Default.ForeignTitleCommitArgs, EngineSocketJson.Default.ForeignTitleReply,
            (args, call) => ForeignTitles<Func<IWriteScope, ForeignTitleReply>>(call, async op =>
            {
                var request = TitleRequest(args.Request); var probe = ProbeTitle(request, op.Token);
                if (probe.Kind != ForeignTitleReplyKind.PartsRequired)
                    return op.Return<Func<IWriteScope, ForeignTitleReply>>(_ => Bounded(call, probe, EngineSocketJson.Default.ForeignTitleReply));
                ForeignTitlePayload? prepared = await op.AdmitForeignTitle(new(request, args.Handle), runtime.Commands, runtime.Machine);
                var registrationError = prepared.Registration is { } title ? Prepare(new(request.Registration!.CommandId, request.FromMachine,
                    request.EntityId, request.Registration.ExpectedVersion, request.Registration.Epoch, title.Command)) : null;
                return scope =>
                {
                    try
                    {
                        CommandResult result;
                        if (prepared!.Batch is { } batch) result = runtime.Commands.ApplyForeignAncestryBatch(scope, batch);
                        else
                        {
                            var query = request.Registration!;
                            var context = new CommandContext(query.CommandId, runtime.Machine, new(ActorKind.System, "machine:" + query.FromMachine),
                                query.EntityId, query.ExpectedVersion, query.Epoch, time.GetUtcNow().ToUnixTimeMilliseconds());
                            result = runtime.Commands.ApplyPreparedSessionTitle(scope, context, prepared.Registration!, registrationError);
                        }
                        return Bounded(call, new ForeignTitleReply(ForeignTitleReplyKind.Applied, null, result, request.Ancestry?.StructureSha256, null), EngineSocketJson.Default.ForeignTitleReply);
                    }
                    catch (ForeignAncestryRefused refusal) { throw new CallFailed(refusal.Result.Error!); }
                    finally { prepared = null; }
                };
            }), order: args => args.Request.FromMachine, bulk: true);
    }

    private ForeignTitleRequest TitleRequest(ForeignTitleRequest args)
    {
        var request = ForeignTitlePayload.Freeze(args);
        if (request.TargetMachine != runtime.Machine) throw new CallFailed("wrong_machine");
        return request;
    }
    private ForeignTitleReply ProbeTitle(ForeignTitleRequest request, CancellationToken ct) =>
        ((IEngineRuntime)runtime).Read(scope => request.Operation == ForeignTitleOperation.RegisterForeign
            ? runtime.Commands.ReadForeignRegisterTitleAck(scope, request.Registration!)
            : runtime.Commands.ReadForeignAncestryTitleAck(scope, request.Ancestry!), ct);
    private async Task<T> ForeignTitles<T>(CallContext call, Func<RelayMessageParts.Operation, Task<T>> body)
    {
        try { return await Relay(call, body).ConfigureAwait(false); }
        catch (ForeignAncestryRefused refusal) { throw new CallFailed(refusal.Result.Error!); }
    }
}
