using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private async Task ResolveSessionTitleAfter(Task ready, SessionPublicationOwner owner, SessionPublication item, SessionTitleResolution resolution)
    {
        await ready;
        var entered = false;
        byte[]? bytes = null;
        string? title = null;
        try
        {
            var ct = resolution.Stop.Token;
            await _sessionTitleSerial.WaitAsync(ct); entered = true;
            var identity = await owner.Identity.Task.WaitAsync(ct);
            OwnerConnectionBinding binding;
            await _socketSend.WaitAsync(ct);
            try
            {
                RequirePublication(owner, item, resolution);
                if (identity != resolution.Reference.PublisherEngineMachine) throw new CallFailed("title_changed");
                var socket = RequireEngine(identity);
                if (!ReferenceEquals(socket, owner.Socket)) throw new CallFailed("title_changed");
                binding = CurrentOwnerBinding(identity, socket); RequireOwnerBinding(binding);
            }
            finally { _socketSend.Release(); }
            var original = (Identity: identity, Socket: owner.Socket);
            await MarkTitleRead(owner, item, resolution, ct);
            var stat = (await ReadOwnerOnAsync("legacy_session_title", JsonSerializer.SerializeToElement(new LegacySessionTitleStatArgs(resolution.Reference),
                SessionsJson.Default.LegacySessionTitleStatArgs), false, original, ct, binding)).Deserialize(SessionsJson.Default.LegacySessionTitleStat)
                ?? throw new IOException("invalid legacy title stat");
            if (!SameTitleKey(stat.Reference, resolution.Reference)) throw new CallFailed("title_changed");
            bytes = new byte[checked((int)resolution.Reference.Utf8Bytes)];
            long offset = 0;
            while (offset < bytes.LongLength)
            {
                await MarkTitleRead(owner, item, resolution, ct);
                var args = new LegacySessionTitleChunkArgs(resolution.Reference, offset, 1024 * 1024);
                var part = (await ReadOwnerOnAsync("legacy_session_title_chunk", JsonSerializer.SerializeToElement(args,
                    SessionsJson.Default.LegacySessionTitleChunkArgs), true, original, ct, binding)).Deserialize(SessionsJson.Default.LegacySessionTitleChunk)
                    ?? throw new IOException("invalid legacy title chunk");
                var expected = Math.Min(1024 * 1024, bytes.LongLength - offset);
                var end = checked(offset + expected);
                if (part.Offset != offset || part.TotalBytes != bytes.LongLength || part.Data.LongLength != expected ||
                    part.NextOffset != (end < bytes.LongLength ? end : null)) throw new IOException("invalid legacy title offsets");
                part.Data.CopyTo(bytes, checked((int)offset)); offset = end;
            }
            if (SessionTitleHash.Compute(bytes) != resolution.Reference.Sha256) throw new IOException("legacy title hash differs");
            title = new UTF8Encoding(false, true).GetString(bytes);
            await _socketSend.WaitAsync(ct);
            try
            {
                RequirePublication(owner, item, resolution); RequireOwnerBinding(binding);
                // SAME-key pushes replace metadata and ordinal, never the verified content or its prefix.
                ApplySession(ResolvedSessionItem(item.Notice!.Value, title));
                item.Resolution = null; item.Notice = null; item.Reference = null;
            }
            finally { _socketSend.Release(); }
        }
        catch (OperationCanceledException) when (resolution.Stop.IsCancellationRequested || resolution.Revoked || owner.Revoked) { }
        catch (Exception error)
        {
            SessionTitleFailure(error);
            await _socketSend.WaitAsync();
            try
            {
                if (!owner.Revoked && ReferenceEquals(item.Resolution, resolution))
                {
                    item.Resolution = null;
                    if (error is CallFailed { Message: "title_changed" or "unknown session" } &&
                        item.Ordinal > resolution.IssuedOrdinal && item.Ordinal > item.AttemptedOrdinal &&
                        SameTitleKey(item.Reference!, resolution.Reference)) QueueResolution(owner, item);
                }
            }
            finally { _socketSend.Release(); }
        }
        finally
        {
            Task cancellation;
            await _socketSend.WaitAsync();
            try
            {
                if (ReferenceEquals(item.Resolution, resolution)) item.Resolution = null;
                cancellation = resolution.Cancellation;
            }
            finally { _socketSend.Release(); }
            try { await cancellation; await owner.Cancellation; }
            finally
            {
                resolution.Stop.Dispose(); bytes = null; title = null;
                if (entered) _sessionTitleSerial.Release();
            }
        }
    }

    private void RequirePublication(SessionPublicationOwner owner, SessionPublication item, SessionTitleResolution resolution)
    {
        if (owner.Revoked || resolution.Revoked || !ReferenceEquals(_sessionPublication, owner) ||
            !ReferenceEquals(_socket, owner.Socket) || owner.Socket.State != WebSocketState.Open || owner.Lifecycle != _ownerLifecycle ||
            !owner.Items.TryGetValue(item.Id, out var current) || !ReferenceEquals(current, item) ||
            !ReferenceEquals(item.Resolution, resolution) || item.Reference is null || !SameTitleKey(item.Reference, resolution.Reference))
            throw new OperationCanceledException(resolution.Stop.Token);
    }
    private async Task MarkTitleRead(SessionPublicationOwner owner, SessionPublication item, SessionTitleResolution resolution, CancellationToken ct)
    {
        await _socketSend.WaitAsync(ct);
        try { RequirePublication(owner, item, resolution); resolution.IssuedOrdinal = item.Ordinal; }
        finally { _socketSend.Release(); }
    }
    private static SessionItem ResolvedSessionItem(JsonElement metadata, string title) => new(
        metadata.GetProperty("sessionId").GetString()!, metadata.GetProperty("agent").GetString()!, metadata.GetProperty("repoUrl").GetString()!, title,
        OptionalText(metadata, "machine"), OptionalText(metadata, "parentSessionId"), OptionalText(metadata, "acpSessionId"),
        metadata.GetProperty("updatedAt").GetInt64(), metadata.GetProperty("state").GetString()!, OptionalText(metadata, "activeRunId"),
        metadata.GetProperty("pendingAsk").GetBoolean(), metadata.GetProperty("background").GetInt32(), metadata.GetProperty("needsAttention").GetBoolean(),
        metadata.GetProperty("unread").GetBoolean(), OptionalNumber(metadata, "completedAt"), OptionalNumber(metadata, "settledAt"),
        OptionalText(metadata, "root"), OptionalText(metadata, "titleSource"));
    private static long? OptionalNumber(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt64() : null;
}
