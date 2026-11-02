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
                if (prepared is null) operation.Return(true);
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
}
