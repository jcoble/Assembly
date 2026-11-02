using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Engine.Relays;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private async Task<JsonElement> BulkCallAsync(string feature, string method, JsonElement args, CancellationToken ct, string? expectedEngineId, ClientWebSocket? expectedConnection)
    {
        await _bulkCall.WaitAsync(ct);
        try { return await BulkCallHeldAsync(feature, method, args, ct, expectedEngineId, expectedConnection); }
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
                startIntent?.SendingBulk(this, _bulkSocket, feature, method, args, id);
                await SendFrameAsync(_bulkSocket, $"{{\"t\":\"call\",\"id\":{id},\"feature\":\"{feature}\",\"method\":\"{method}\",\"args\":{args.GetRawText()}}}", ct);
                startIntent?.FrameOrdered();
            }
            finally { _socketSend.Release(); }
            var frame = await ReadFrameAsync(_bulkSocket, ct);
            if (frame.GetProperty("id").GetInt64() != id) throw new IOException("remote bulk response id differs");
            if (frame.GetProperty("t").GetString() != "result") throw new CallFailed(frame.GetProperty("message").GetString()!);
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
            return frame.GetProperty("result");
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
