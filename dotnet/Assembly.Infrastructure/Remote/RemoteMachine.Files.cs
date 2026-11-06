using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Files;
using Assembly.Infrastructure.Engine;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    // Files shares the existing transports. It never reconnects an established resource scope.
    // A path target's channel has no mirror binding (live is null); its owner binding alone fences it.
    internal async Task<FileRelayChannel> OpenFilesAsync(OwnerConnectionBinding binding,
        EngineMirror.LiveMirrorBinding? live, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Settle the small grant exchange even if its desktop caller cancels. Otherwise a successful late
        // reply could leave an unknown scope on the still-live normal relay socket. Connection loss closes it.
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(binding.Invalidated, _socketStop.Token);
        await _socketSend.WaitAsync(opening.Token);
        try { RequireOwnerBinding(binding); RequireLiveMirror(binding, live); }
        finally { _socketSend.Release(); }
        // Finish the grant under the exact normal binding even if its mirror changes meanwhile.
        // The known grant is then closed below; file resources still require the guarded attachment.
        var answer = await CallAsync("files", "openScope", JsonSerializer.SerializeToElement(new FileScopeOpen(),
            FilesJson.Default.FileScopeOpen), false, opening.Token, binding.Machine, expectedOwner: binding);
        var grant = answer.Deserialize(FilesJson.Default.FileScopeGrant) ?? throw new CallFailed("Invalid file relay scope.");
        try
        {
            ct.ThrowIfCancellationRequested();
            await _bulkCall.WaitAsync(ct);
            try
            {
                await BulkCallHeldAsync("files", "attachScope", JsonSerializer.SerializeToElement(
                    new FileScopeAttach(grant.Scope, grant.Attachment), FilesJson.Default.FileScopeAttach), ct,
                    binding.Machine, null, binding, live);
                await _socketSend.WaitAsync(ct);
                try
                {
                    RequireOwnerBinding(binding);
                    RequireLiveMirror(binding, live);
                    if (_bulkSocket is not { State: WebSocketState.Open } bulk || _bulkEngineIdentity != binding.Machine)
                        throw new CallFailed("The file relay connection changed.", "files_scope_changed");
                    return new FileRelayChannel(this, binding, live, bulk, grant.Scope);
                }
                finally { _socketSend.Release(); }
            }
            finally { _bulkCall.Release(); }
        }
        catch
        {
            // The original normal owner remains responsible for an attachment that failed or was cancelled.
            try { await CloseFilesAsync(binding, grant.Scope, _socketStop.Token); }
            catch (Exception error) when (error is CallFailed or IOException or WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            throw;
        }
    }

    private Task<JsonElement> CloseFilesAsync(OwnerConnectionBinding binding,
        string scope, CancellationToken ct) => CallAsync("files", "closeScope", JsonSerializer.SerializeToElement(
            new FileScopeClose(scope), FilesJson.Default.FileScopeClose), false, ct, binding.Machine,
            expectedOwner: binding);

    internal sealed class FileRelayChannel(RemoteMachine owner, OwnerConnectionBinding binding,
        EngineMirror.LiveMirrorBinding? live, ClientWebSocket bulk, string scope)
    {
        internal string Scope { get; } = scope;
        internal string Machine => binding.Machine;
        private int ended;

        private void RequireCurrent()
        {
            if (Volatile.Read(ref ended) != 0 || !ReferenceEquals(owner._bulkSocket, bulk) ||
                bulk.State != WebSocketState.Open || owner._bulkEngineIdentity != binding.Machine)
                throw new CallFailed("The file relay connection changed. Reopen the file.", "files_scope_changed");
            owner.RequireOwnerBinding(binding);
            RequireLiveMirror(binding, live);
        }

        internal async Task<JsonElement> CallAsync(string method, JsonElement args, bool isBulk, CancellationToken ct)
        {
            if (isBulk)
            {
                await owner._bulkCall.WaitAsync(ct);
                try
                {
                    RequireCurrent();
                    var answer = await owner.BulkCallHeldAsync("files", method, args, ct, binding.Machine,
                        null, binding, live);
                    RequireCurrent();
                    return answer;
                }
                finally { owner._bulkCall.Release(); }
            }
            await owner._socketSend.WaitAsync(ct);
            try { RequireCurrent(); }
            finally { owner._socketSend.Release(); }
            var result = await owner.CallAsync("files", method, args, false, ct, binding.Machine,
                expectedOwner: binding, expectedMirror: live);
            await owner._socketSend.WaitAsync(ct);
            try { RequireCurrent(); }
            finally { owner._socketSend.Release(); }
            return result;
        }

        internal async Task CloseAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            // Even if the bulk peer closed first, close on the exact original normal owner; never reopen bulk.
            await owner.CloseFilesAsync(binding, Scope, ct);
        }
    }
}
