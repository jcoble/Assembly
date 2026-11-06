using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Infrastructure.Chat.Files;

internal sealed partial class FilesFeature
{
    private async Task<SourceCall?> CaptureSource<T>(string method, T args, JsonTypeInfo<T> type, CallContext call)
    {
        if (method is "release" or "unobserve" or "releaseSearch" or "dropSave") return null;
        var json = JsonSerializer.SerializeToElement(args, type);
        if (!json.TryGetProperty("expectedSource", out var expected) || expected.ValueKind == JsonValueKind.Null)
            return null;
        // Session-less continuations are admitted by their original connection-owned scope, never a new lookup; a path
        // target has no session source to capture and checks its own peer.
        if (!json.TryGetProperty("session", out var session) || session.ValueKind == JsonValueKind.Null) return null;
        if (call.Peer is not OwnerPeer) throw new CallFailed("forbidden");
        // A viewed folder has no session row to bind: the forward that named this machine already chose it.
        if (FolderView.IsView(session.GetString())) return null;
        var source = expected.Deserialize(FilesJson.Default.ExpectedSource) ?? throw new CallFailed("source_changed");
        if (sourceBindings is null) throw new CallFailed("The file source cannot be verified.", "source_changed");
        var guard = await sourceBindings.CaptureAsync(source, session.GetString()!, call.Cancellation);
        try { guard.EnsureDisplayCurrent(); }
        catch { guard.Dispose(); throw; }
        // The existing socket reply owner fences delivery and disposes the same guard after the reply.
        call.SourceGuard = guard;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Cancellation, guard.Changed);
        return new SourceCall(call with { Cancellation = stop.Token }, source.Machine, guard, stop);
    }

    private sealed class SourceCall(CallContext context, string machine, RowSourceBinding guard,
        CancellationTokenSource stop) : IDisposable
    {
        internal CallContext Context { get; } = context;
        internal void Confirm(bool write)
        {
            try { guard.EnsureDisplayCurrent(); }
            catch (CallFailed) when (write)
            { throw new CallUncertain(machine, "The file operation may have completed before the source changed. Inspect it before trying again."); }
        }
        public void Dispose() => stop.Dispose();
    }
}
