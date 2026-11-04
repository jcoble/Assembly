using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Engine.Relays;
using Assembly.Infrastructure.Socket;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private async Task<JsonElement> BulkCallAsync(string feature, string method, JsonElement args, CancellationToken ct, string? expectedEngineId, ClientWebSocket? expectedConnection,
        OwnerConnectionBinding? expectedOwner = null, Assembly.Infrastructure.Engine.EngineMirror.LiveMirrorBinding? expectedMirror = null)
    {
        await _bulkCall.WaitAsync(ct);
        try { return await BulkCallHeldAsync(feature, method, args, ct, expectedEngineId, expectedConnection, expectedOwner, expectedMirror); }
        finally { _bulkCall.Release(); }
    }

    // The existing bulk exchange, shared by ordinary calls and the fixed relay lease.
    private async Task<JsonElement> BulkCallHeldAsync(string feature, string method, JsonElement args, CancellationToken ct,
        string? expectedEngineId, ClientWebSocket? expectedConnection, OwnerConnectionBinding? relayBinding = null, Assembly.Infrastructure.Engine.EngineMirror.LiveMirrorBinding? relayMirror = null, FrozenMachineStartIntent? startIntent = null)
    {
        ClientWebSocket? live = null;
        try
        {
            if (expectedEngineId is not null)
            {
                await _socketSend.WaitAsync(ct);
                try
                {
                    live = relayBinding is null ? RequireEngine(expectedEngineId) : RequireOwnerBinding(relayBinding);
                    if (relayBinding is not null) RequireLiveMirror(relayBinding, relayMirror);
                    if (expectedConnection is not null && !ReferenceEquals(live, expectedConnection))
                        throw Unreachable("machine socket changed", $"{_name}: owning engine connection changed before bulk send");
                }
                finally { _socketSend.Release(); }
            }
            if (_bulkSocket is not { State: WebSocketState.Open })
            {
                _bulkSocket?.Dispose();
                _bulkSocket = new ClientWebSocket();
                _bulkEngineIdentity = await OpenSocketAsync(_bulkSocket, "/socket/bulk", ct);
            }
            var id = Interlocked.Increment(ref _nextSub);
            await _socketSend.WaitAsync(ct);
            try
            {
                if (expectedEngineId is not null && (!ReferenceEquals(live, RequireEngine(expectedEngineId)) || _bulkEngineIdentity != expectedEngineId))
                    throw Unreachable("unverified engine peer", $"{_name}: bulk socket engine identity differs or is unverified");
                if (relayBinding is not null) { RequireOwnerBinding(relayBinding); RequireLiveMirror(relayBinding, relayMirror); }
                RequireMatchingBackendForStart(feature, method, args);
                startIntent?.SendingBulk(this, _bulkSocket, feature, method, args, id);
                await SendFrameAsync(_bulkSocket, $"{{\"t\":\"call\",\"id\":{id},\"feature\":\"{feature}\",\"method\":\"{method}\",\"args\":{args.GetRawText()}}}", ct);
                startIntent?.FrameOrdered();
            }
            finally { _socketSend.Release(); }
            // Keep the established receive alive while forwarding cancellation, then join its
            // settlement before another call can take the bulk exchange.
            var response = ReadFrameAsync(_bulkSocket, _socketStop.Token);
            JsonElement frame;
            try { frame = await response.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                using var settlement = new CancellationTokenSource(SocketDispatcher.Limit, _time);
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(settlement.Token, _socketStop.Token);
                try
                {
                    await SendFrameAsync(_bulkSocket, $"{{\"t\":\"cancel\",\"id\":{id}}}", stopping.Token);
                    await response.WaitAsync(stopping.Token);
                }
                catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
                {
                    _bulkSocket.Abort();
                }
                // Aborting a peer that did not settle also joins this call's one receive task.
                try { await response; }
                catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
                throw;
            }
            if (frame.GetProperty("id").GetInt64() != id) throw new IOException("remote bulk response id differs");
            var succeeded = frame.GetProperty("t").GetString() == "result";
            if (!succeeded && relayBinding is null && !(feature == "engine" && method == "entities"))
                throw new CallFailed(frame.GetProperty("message").GetString()!);
            if (expectedEngineId is not null)
            {
                await _socketSend.WaitAsync(ct);
                try
                {
                    if (!ReferenceEquals(_socket, live) || live is not { State: WebSocketState.Open })
                        throw Unreachable("machine socket changed", $"{_name}: machine socket changed during bulk read");
                    RequireEngine(expectedEngineId);
                    if (relayBinding is not null) { RequireOwnerBinding(relayBinding); RequireLiveMirror(relayBinding, relayMirror); }
                    if (_bulkEngineIdentity != expectedEngineId)
                        throw Unreachable("unverified engine peer", $"{_name}: bulk socket engine identity changed");
                }
                finally { _socketSend.Release(); }
            }
            if (!succeeded)
                throw new CallFailed(frame.GetProperty("message").GetString()!,
                    (feature == "engine" && method == "entities" || feature == "messages" && method is "sender_order" or "receiver_order") &&
                    frame.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                        ? code.GetString() : null);
            return frame.GetProperty("result");
        }
        // A refused call is a completed exchange, so both machine connections remain usable.
        catch (CallFailed) { startIntent?.SendFailed(); throw; }
        catch (ObjectDisposedException) when (_socketStop.IsCancellationRequested)
        {
            startIntent?.SendFailed();
            throw new OperationCanceledException(_socketStop.Token);
        }
        catch (Exception error)
        {
            startIntent?.SendFailed();
            if (relayBinding is null && !ct.IsCancellationRequested && error is not MachineUnreachable)
            {
                await _socketSend.WaitAsync(CancellationToken.None);
                try { if (expectedEngineId is null || ReferenceEquals(_socket, live)) _socket?.Abort(); }
                finally { _socketSend.Release(); }
            }
            _bulkSocket?.Dispose();
            _bulkSocket = null;
            _bulkEngineIdentity = null;
            throw;
        }
    }

}
