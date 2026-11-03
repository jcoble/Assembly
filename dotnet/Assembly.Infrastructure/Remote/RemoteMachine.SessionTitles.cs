using Assembly.Infrastructure.Engine;
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
        try
        {
            var ct = resolution.Token;
            await _sessionTitleSerial.WaitAsync(ct); entered = true;
            var verified = await VerifiedSessionTitle.ReadAsync(this, item.Id, ct, ct, resolution.ExpectedOwner, resolution.Live);
            await CommitVerifiedSessionTitleAsync(verified, ct);
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
                        SameTitleKey(item.Reference!, resolution.Reference)) QueueResolution(owner, item, resolution.ExpectedOwner, resolution.Live, resolution.RequestCancellation);
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
                resolution.Stop.Dispose();
                if (entered) _sessionTitleSerial.Release();
            }
        }
    }

    // These are the same fixed production phases used by the owned resolver above.
    internal Task<VerifiedSessionTitle> ReadVerifiedSessionTitleAsync(string sessionId, CancellationToken ct) =>
        ReadVerifiedSessionTitleAsync(sessionId, ct, null);
    private Task<VerifiedSessionTitle> ReadVerifiedSessionTitleAsync(string sessionId, CancellationToken ct, CancellationToken? expectedResolution) =>
        VerifiedSessionTitle.ReadAsync(this, sessionId, ct, expectedResolution);
    internal Task CommitVerifiedSessionTitleAsync(VerifiedSessionTitle verified, CancellationToken ct) => verified.CommitAsync(this, ct);

    internal sealed class VerifiedSessionTitle
    {
        private readonly RemoteMachine _machine;
        private readonly SessionPublicationOwner _owner;
        private readonly SessionPublication _item;
        private readonly SessionTitleResolution _resolution;
        private readonly OwnerConnectionBinding _binding;
        private readonly string _title;
        private readonly EngineMirror.LiveMirrorBinding? _live;
        private readonly OwnerConnectionBinding? _expectedOwner;
        private VerifiedSessionTitle(RemoteMachine machine, SessionPublicationOwner owner, SessionPublication item,
            SessionTitleResolution resolution, OwnerConnectionBinding binding, string title, EngineMirror.LiveMirrorBinding? live, OwnerConnectionBinding? expectedOwner)
        {
            _machine = machine; _owner = owner; _item = item; _resolution = resolution; _binding = binding; _title = title; _live = live; _expectedOwner = expectedOwner;
        }
        internal static async Task<VerifiedSessionTitle> ReadAsync(RemoteMachine machine, string sessionId, CancellationToken ct,
            CancellationToken? expectedResolution, OwnerConnectionBinding? expectedOwner = null,
            EngineMirror.LiveMirrorBinding? live = null)
        {
            SessionPublicationOwner owner;
            SessionPublication item;
            SessionTitleResolution resolution;
            await machine._socketSend.WaitAsync(ct);
            try
            {
                owner = machine._sessionPublication ?? throw new OperationCanceledException(ct);
                if (!owner.Items.TryGetValue(sessionId, out item!) || item.Resolution is not { } current ||
                    (expectedResolution is { } expected && expected != current.Token)) throw new OperationCanceledException(ct);
                resolution = current;
                machine.RequirePublication(owner, item, resolution);
            }
            finally { machine._socketSend.Release(); }
            var identity = await owner.Identity.Task.WaitAsync(ct);
            OwnerConnectionBinding binding;
            await machine._socketSend.WaitAsync(ct);
            try
            {
                machine.RequirePublication(owner, item, resolution);
                if (identity != resolution.Reference.PublisherEngineMachine) throw new CallFailed("title_changed");
                var socket = machine.RequireEngine(identity);
                if (!ReferenceEquals(socket, owner.Socket)) throw new CallFailed("title_changed");
                binding = machine.CurrentOwnerBinding(identity, socket); machine.RequireOwnerBinding(binding);
                if (expectedOwner is not null && !ReferenceEquals(binding, expectedOwner))
                    throw new CallFailed("The accepting owner connection changed.", "source_changed");
                RequireLiveMirror(binding, live);
            }
            finally { machine._socketSend.Release(); }
            var original = (Identity: identity, Socket: owner.Socket);
            Task<JsonElement> Read(string method, JsonElement args, bool bulk) => live is null
                ? machine.ReadOwnerOnAsync(method, args, bulk, original, ct, binding)
                : machine.CallAsync("engine", method, args, bulk, ct, original.Identity, original.Socket,
                    expectedOwner: binding, expectedMirror: live);
            await machine.MarkTitleRead(owner, item, resolution, ct);
            var stat = (await Read("legacy_session_title", JsonSerializer.SerializeToElement(new LegacySessionTitleStatArgs(resolution.Reference),
                SessionsJson.Default.LegacySessionTitleStatArgs), false)).Deserialize(SessionsJson.Default.LegacySessionTitleStat)
                ?? throw new IOException("invalid legacy title stat");
            if (!SameTitleKey(stat.Reference, resolution.Reference)) throw new CallFailed("title_changed");
            var bytes = new byte[checked((int)resolution.Reference.Utf8Bytes)];
            long offset = 0;
            while (offset < bytes.LongLength)
            {
                await machine.MarkTitleRead(owner, item, resolution, ct);
                var args = new LegacySessionTitleChunkArgs(resolution.Reference, offset, 1024 * 1024);
                var part = (await Read("legacy_session_title_chunk", JsonSerializer.SerializeToElement(args,
                    SessionsJson.Default.LegacySessionTitleChunkArgs), true)).Deserialize(SessionsJson.Default.LegacySessionTitleChunk)
                    ?? throw new IOException("invalid legacy title chunk");
                var expected = Math.Min(1024 * 1024, bytes.LongLength - offset);
                var end = checked(offset + expected);
                if (part.Offset != offset || part.TotalBytes != bytes.LongLength || part.Data.LongLength != expected ||
                    part.NextOffset != (end < bytes.LongLength ? end : null)) throw new IOException("invalid legacy title offsets");
                part.Data.CopyTo(bytes, checked((int)offset)); offset = end;
            }
            if (SessionTitleHash.Compute(bytes) != resolution.Reference.Sha256) throw new IOException("legacy title hash differs");
            return new(machine, owner, item, resolution, binding, new UTF8Encoding(false, true).GetString(bytes), live, expectedOwner);
        }
        internal async Task CommitAsync(RemoteMachine machine, CancellationToken ct)
        {
            if (!ReferenceEquals(machine, _machine) || machine._ownerStopping || _owner.Revoked || _resolution.Revoked || _resolution.Token.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            await machine._socketSend.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                machine.RequirePublication(_owner, _item, _resolution); machine.RequireOwnerBinding(_binding);
                RequireLiveMirror(_binding, _live);
                // SAME-key pushes replace metadata and ordinal, never the verified content or its prefix.
                machine.ApplySession(ResolvedSessionItem(_item.Notice!.Value, _title), _expectedOwner, _live, ct);
                _item.Resolution = null; _item.Notice = null; _item.Reference = null;
            }
            finally { machine._socketSend.Release(); }
        }
    }

    private void RequirePublication(SessionPublicationOwner owner, SessionPublication item, SessionTitleResolution resolution)
    {
        if (owner.Revoked || resolution.Revoked || !ReferenceEquals(_sessionPublication, owner) ||
            !ReferenceEquals(_socket, owner.Socket) || owner.Socket.State != WebSocketState.Open || owner.Lifecycle != _ownerLifecycle ||
            !owner.Items.TryGetValue(item.Id, out var current) || !ReferenceEquals(current, item) ||
            !ReferenceEquals(item.Resolution, resolution) || item.Reference is null || !SameTitleKey(item.Reference, resolution.Reference))
            throw new OperationCanceledException(resolution.Token);
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
