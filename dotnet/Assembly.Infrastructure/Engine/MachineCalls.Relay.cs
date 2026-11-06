using Assembly.Engine.NeedsMe;
using Assembly.Engine.Contracts;
using Assembly.Infrastructure.Engine.Relays;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;
using Assembly.Engine;
using Assembly.Engine.Hosting;
using Assembly.Engine.Relays;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Engine;

public sealed partial class MachineCalls : IAsyncDisposable
{
    private readonly RelayMessageParts _relayParts = new();
    public ValueTask DisposeAsync() => _relayParts.DisposeAsync();

    private void RegisterRelay(ICallRegistry calls)
    {
        calls.Closed(_relayParts.Closed);
        calls.Read("engine", "relay_source_open", EngineSocketJson.Default.RelaySourceReadArgs, EngineSocketJson.Default.RelaySourceOpened,
            (args, call) => Relay(call, operation =>
            {
                var capture = runtime.Reader.RelaySource(args, operation.Token);
                Bounded(call, new RelaySourceOpened(new string('0', 32), RelayDigests.Describe(capture)), EngineSocketJson.Default.RelaySourceOpened);
                var opened = operation.PublishSource(capture);
                return Task.FromResult(Bounded(call, opened, EngineSocketJson.Default.RelaySourceOpened));
            }), bulk: true);
        calls.Read("engine", "relay_source_part", EngineSocketJson.Default.RelaySourcePartArgs, EngineSocketJson.Default.RelaySourcePart,
            (args, call) => Relay(call, async operation => Bounded(call, await operation.ReadSource(args), EngineSocketJson.Default.RelaySourcePart)), bulk: true);
        calls.Read("engine", "relay_source_revalidate", EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.RelaySourceDescriptor,
            (args, call) => Relay(call, operation =>
            {
                var original = operation.SourceDescriptor(args.Handle);
                var current = RelayDigests.Describe(runtime.Reader.RelaySource(new(original.MessageId, original.RecipientMachine), operation.Token));
                if (current != original) throw new CallFailed("source_changed");
                return Task.FromResult(operation.Return(Bounded(call, current, EngineSocketJson.Default.RelaySourceDescriptor)));
            }), bulk: true);
        calls.Read("engine", "relay_source_close", EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean,
            (args, call) => Relay(call, operation => { operation.CloseSource(args.Handle); return Task.FromResult(true); }), bulk: true);
        calls.Read("engine", "relay_send_ack", EngineSocketJson.Default.RelaySourceDescriptor, EngineSocketJson.Default.RelaySendAck,
            (args, call) => Relay(call, operation => Task.FromResult(operation.Return(Bounded(call,
                ((IEngineRuntime)runtime).Read(s => runtime.Commands.ReadRelayAck(s, args), operation.Token), EngineSocketJson.Default.RelaySendAck)))));
        calls.Write("engine", "relay_send_begin", EngineSocketJson.Default.RelaySendRequest, EngineSocketJson.Default.RelaySendBeginReply,
            async (args, call) =>
            {
                var result = await Relay(call, operation =>
                {
                    var probe = ((IEngineRuntime)runtime).Read(s => runtime.Commands.ProbeRelaySend(s, args), operation.Token);
                    Bounded(call, probe with { Transfer = probe.Status == "parts_required" ? new string('0', 32) : null }, EngineSocketJson.Default.RelaySendBeginReply);
                    return Task.FromResult(Bounded(call, operation.PublishTarget(args, probe), EngineSocketJson.Default.RelaySendBeginReply));
                });
                return _ => result;
            }, bulk: true);
        calls.Write("engine", "relay_send_part", EngineSocketJson.Default.RelaySendPartArgs, EngineSocketJson.Default.RelaySendPartAccepted,
            async (args, call) =>
            {
                var result = await Relay(call, operation => operation.Append(args));
                return _ => result;
            }, bulk: true);
        calls.Write("engine", "relay_send_drop", EngineSocketJson.Default.RelayHandleArgs, EngineSocketJson.Default.Boolean,
            async (args, call) =>
            {
                var result = await Relay(call, operation => { operation.DropTarget(args.Handle); return Task.FromResult(true); });
                return _ => result;
            }, bulk: true);
        calls.Write("engine", "relay_send_commit", EngineSocketJson.Default.RelaySendCommitArgs, EngineSocketJson.Default.RelaySendAck,
            (args, call) => Relay<Func<IWriteScope, RelaySendAck>>(call, async operation =>
            {
                var probe = ((IEngineRuntime)runtime).Read(s => runtime.Commands.ProbeRelaySend(s, args.Request), operation.Token);
                PreparedRelaySend? prepared = probe.Status == "parts_required" ? await operation.Admit(args) : null;
                if (prepared is null)
                {
                    if (args.Transfer is not null) operation.DropTarget(args.Transfer);
                    operation.Return(true);
                }
                // Admission has detached immutable bytes. This closure remains valid after connection loss.
                return scope =>
                {
                    try { return Bounded(call, runtime.Commands.ApplyRelaySend(scope, args.Request, prepared, time.GetUtcNow().ToUnixTimeMilliseconds()), EngineSocketJson.Default.RelaySendAck); }
                    catch (RelayRefused refusal) { throw new CallFailed(refusal.Error); }
                    finally { prepared = null; }
                };
            }), bulk: true);
        calls.Write("engine", "relay_outcome", EngineSocketJson.Default.RelayOutcomeArgs, EngineJson.Default.CommandResult,
            (args, call) => Relay<Func<IWriteScope, Assembly.Engine.Contracts.CommandResult>>(call, operation => Task.FromResult(operation.Return<Func<IWriteScope, Assembly.Engine.Contracts.CommandResult>>(scope =>
            {
                try { return runtime.Commands.ApplyRelayOutcome(scope, args, time.GetUtcNow().ToUnixTimeMilliseconds()); }
                catch (RelayRefused refusal) { throw new CallFailed(refusal.Error); }
            }))));
    }

    private async Task<T> Relay<T>(CallContext call, Func<RelayMessageParts.Operation, Task<T>> body)
    {
        using var operation = _relayParts.Enter(call);
        try { return await body(operation).ConfigureAwait(false); }
        catch (RelayRefused refusal) { throw new CallFailed(refusal.Error); }
        catch (System.Text.DecoderFallbackException) { throw new CallFailed("bad_parts"); }
    }
    private static T Bounded<T>(CallContext call, T result, JsonTypeInfo<T> type)
    {
        if (SocketDispatcher.Result(call.ReplyId ?? long.MinValue, JsonSerializer.SerializeToElement(result, type)).Length > 4 * 1024 * 1024)
            throw new CallFailed("frame_too_large");
        return result;
    }

    private void RegisterParentNeeds(ICallRegistry calls)
    {
        RegisterParentNeedParts(calls);
        calls.Read("engine", "parent_need_pending", EngineSocketJson.Default.ParentNeedPendingArgs,
            EngineSocketJson.Default.ParentNeedPendingPage, (args, call) => Relay(call, operation =>
                Task.FromResult(operation.Return(runtime.Reader.ParentNeedPending(args,
                    page => Socket.SocketDispatcher.Result(call.ReplyId ?? long.MinValue,
                        System.Text.Json.JsonSerializer.SerializeToElement(page, EngineSocketJson.Default.ParentNeedPendingPage)).Length,
                    operation.Token)))));
        calls.Read("engine", "need_answer_capture", EngineJson.Default.ParentNeedSource,
            EngineSocketJson.Default.ParentNeedCaptured, (args, call) => Relay(call, operation =>
                Task.FromResult(operation.Return(Bounded(call, needs.CaptureParentNeed(args, operation.Token),
                    EngineSocketJson.Default.ParentNeedCaptured)))));
        calls.Read("engine", "need_answer_probe", EngineSocketJson.Default.NeedAnswerRequestDescriptor,
            EngineSocketJson.Default.NeedAnswerAck, (args, call) => Relay(call, operation =>
                Task.FromResult(operation.Return(Bounded(call,
                    ((IEngineRuntime)runtime).Read(scope => runtime.Commands.ReadForwardedParentNeed(scope, args), operation.Token),
                    EngineSocketJson.Default.NeedAnswerAck)))));
        calls.Write("engine", "parent_need_dispatch", EngineSocketJson.Default.ParentNeedDispatchArgs,
            EngineJson.Default.ParentNeedDispatchAck, (args, call) => Relay<Func<IWriteScope, ParentNeedDispatchAck>>(call, operation =>
            {
                var capture = runtime.Reader.ParentNeedInput(args.IntentId, operation.Token);
                if (capture.Version != args.IntentVersion) throw new CallFailed("source_changed");
                var prepared = PreparedParentNeedPhase.Dispatch(capture, args.DescriptorH, operation.Token);
                return Task.FromResult(operation.Return<Func<IWriteScope, ParentNeedDispatchAck>>(scope =>
                    runtime.Commands.DispatchParentNeedWithAck(scope, prepared, time.GetUtcNow().ToUnixTimeMilliseconds())));
            }));
        calls.Write("engine", "parent_need_outcome", EngineSocketJson.Default.ParentNeedOutcomeArgs,
            EngineJson.Default.CommandResult, (args, call) => Relay<Func<IWriteScope, CommandResult>>(call, operation =>
            {
                var capture = runtime.Reader.ParentNeedInput(args.IntentId, operation.Token);
                if (capture.Version != args.IntentVersion) throw new CallFailed("source_changed");
                var prepared = PreparedParentNeedPhase.Complete(capture, args.Outcome, operation.Token, args.RejectedPreparation);
                return Task.FromResult(operation.Return<Func<IWriteScope, CommandResult>>(scope =>
                    runtime.Commands.CompleteParentNeedIntent(scope, prepared, time.GetUtcNow().ToUnixTimeMilliseconds())));
            }));
    }

    private void RegisterParentNeedParts(ICallRegistry calls)
    {
        calls.Read("engine", "parent_need_input_open", EngineSocketJson.Default.ParentNeedInputArgs,
            EngineSocketJson.Default.ParentNeedInputOpened, (args, call) => Relay(call, operation =>
                Task.FromResult(Bounded(call, operation.PublishNeedInput(runtime.Reader.ParentNeedInput(args.IntentId, operation.Token)),
                    EngineSocketJson.Default.ParentNeedInputOpened))), bulk: true);
        calls.Read("engine", "parent_need_input_part", EngineSocketJson.Default.ParentNeedInputPartArgs,
            EngineSocketJson.Default.RelaySourcePart, (args, call) => Relay(call, async operation =>
                Bounded(call, await operation.ReadNeedInput(args), EngineSocketJson.Default.RelaySourcePart)), bulk: true);
        calls.Read("engine", "parent_need_input_close", EngineSocketJson.Default.RelayHandleArgs,
            EngineSocketJson.Default.Boolean, (args, call) => Relay(call, operation =>
            {
                operation.CloseNeedInput(args.Handle); return Task.FromResult(true);
            }), bulk: true);
        calls.Write("engine", "need_answer_stage_open", EngineSocketJson.Default.ParentNeedUploadArgs,
            EngineSocketJson.Default.ParentNeedUploadOpened, async (args, call) =>
            {
                var result = await Relay(call, operation => Task.FromResult(operation.PublishNeedTarget(args)));
                return _ => result;
            }, bulk: true);
        calls.Write("engine", "need_answer_stage_part", EngineSocketJson.Default.ParentNeedUploadPartArgs,
            EngineSocketJson.Default.ParentNeedUploadOffset, async (args, call) =>
            {
                var result = await Relay(call, operation => operation.AppendNeedTarget(args)); return _ => result;
            }, bulk: true);
        calls.Write("engine", "need_answer_stage_drop", EngineSocketJson.Default.RelayHandleArgs,
            EngineSocketJson.Default.Boolean, async (args, call) =>
            {
                await Relay(call, operation => { operation.DropNeedTarget(args.Handle); return Task.FromResult(true); });
                return _ => true;
            }, bulk: true);
        calls.Write("engine", "parent_need_prepare", EngineSocketJson.Default.RelayHandleArgs,
            EngineJson.Default.CommandResult, (args, call) => Relay<Func<IWriteScope, CommandResult>>(call, async operation =>
            {
                var staged = await operation.AdmitNeedTarget(args.Handle, ParentNeedUploadKind.Prepare);
                var payload = JsonSerializer.Deserialize(staged.Bytes, EngineJson.Default.ParentNeedPrepared)
                    ?? throw new CallFailed("bad_args");
                var capture = runtime.Reader.ParentNeedInput(staged.Args.IntentId!, operation.Token);
                if (capture.Version != staged.Args.IntentVersion) throw new CallFailed("source_changed");
                var prepared = PreparedParentNeedPhase.Prepare(capture, payload, operation.Token);
                return operation.Return<Func<IWriteScope, CommandResult>>(scope =>
                    runtime.Commands.PrepareParentNeedIntent(scope, prepared, time.GetUtcNow().ToUnixTimeMilliseconds()));
            }), bulk: true);
        calls.Write("engine", "need_answer_execute", EngineSocketJson.Default.RelayHandleArgs,
            EngineSocketJson.Default.NeedAnswerAck, (args, call) => Relay<Func<IWriteScope, NeedAnswerAck>>(call, async operation =>
            {
                var staged = await operation.AdmitNeedTarget(args.Handle, ParentNeedUploadKind.Execute);
                var payload = JsonSerializer.Deserialize(staged.Bytes, EngineJson.Default.ParentNeedPrepared)
                    ?? throw new CallFailed("bad_args");
                var recorded = ((IEngineRuntime)runtime).Read(scope => runtime.Commands.ReadForwardedParentNeed(scope, payload.Descriptor), operation.Token);
                var prepared = recorded.Result is null ? needs.PrepareForwardedParentNeed(payload, operation.Token) : null;
                return operation.Return<Func<IWriteScope, NeedAnswerAck>>(scope =>
                {
                    if (prepared is not null) runtime.Commands.ApplyForwardedParentNeed(scope, prepared, time.GetUtcNow().ToUnixTimeMilliseconds());
                    return Bounded(call, runtime.Commands.ReadForwardedParentNeed(scope, payload.Descriptor), EngineSocketJson.Default.NeedAnswerAck);
                });
            }), bulk: true);
    }
}
