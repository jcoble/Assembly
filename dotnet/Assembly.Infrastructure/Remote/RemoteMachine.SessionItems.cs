using Assembly.Infrastructure.Engine;
using System.Net.WebSockets;
using System.Text.Json;
using Assembly.Application.Common.Engine;
using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Infrastructure.Remote;

internal sealed partial class RemoteMachine
{
    private readonly SemaphoreSlim _sessionTitleSerial = new(1, 1);
    private readonly List<Task> _sessionTitleDrains = [];
    private SessionPublicationOwner? _sessionPublication;

    private sealed class SessionPublicationOwner(ClientWebSocket socket, long lifecycle, CancellationToken stopping)
    {
        internal readonly ClientWebSocket Socket = socket;
        internal readonly long Lifecycle = lifecycle;
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        internal readonly TaskCompletionSource<string> Identity = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Dictionary<string, SessionPublication> Items = new(StringComparer.Ordinal);
        internal readonly List<Task> Work = [];
        internal Task Cancellation = Task.CompletedTask;
        internal bool Revoked, Negotiated;
    }
    private sealed class SessionPublication(string id)
    {
        internal readonly string Id = id;
        internal long Ordinal, AttemptedOrdinal;
        internal JsonElement? Notice;
        internal LegacySessionTitleRef? Reference;
        internal SessionTitleResolution? Resolution;
    }
    private sealed class SessionTitleResolution
    {
        internal readonly LegacySessionTitleRef Reference;
        internal readonly CancellationTokenSource Stop;
        internal readonly CancellationToken Token;
        internal long IssuedOrdinal;
        internal readonly OwnerConnectionBinding? ExpectedOwner;
        internal readonly EngineMirror.LiveMirrorBinding? Live;
        internal Task Work = Task.CompletedTask;
        internal SessionTitleResolution(SessionPublicationOwner owner, LegacySessionTitleRef reference, long ordinal,
            OwnerConnectionBinding? expectedOwner = null, EngineMirror.LiveMirrorBinding? live = null)
        {
            Reference = reference; IssuedOrdinal = ordinal;
            ExpectedOwner = expectedOwner; Live = live;
            Stop = expectedOwner is null ? CancellationTokenSource.CreateLinkedTokenSource(owner.Stop.Token)
                : live is null ? CancellationTokenSource.CreateLinkedTokenSource(owner.Stop.Token, expectedOwner.Invalidated)
                : CancellationTokenSource.CreateLinkedTokenSource(owner.Stop.Token, expectedOwner.Invalidated, live.Invalidated);
            Token = Stop.Token;
        }
        internal Task Cancellation = Task.CompletedTask;
        internal bool Revoked;
    }

    // All ingress and lifetime admission runs under _socketSend, with no remote await.
    private void BeginSessionPublications(ClientWebSocket socket) => _sessionPublication = new(socket, _ownerLifecycle, _socketStop.Token);
    private Task EndSessionPublications()
    {
        if (_sessionPublication is not { } owner) return Task.CompletedTask;
        _sessionPublication = null; owner.Revoked = true; owner.Identity.TrySetCanceled();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Cancellation = CancelPublicationAfter(ready.Task, owner.Stop);
        var drain = DrainPublicationAfter(ready.Task, owner);
        _sessionTitleDrains.RemoveAll(task => task.IsCompletedSuccessfully);
        _sessionTitleDrains.Add(drain); ready.SetResult(); return drain;
    }
    private async Task CancelPublicationAfter(Task ready, CancellationTokenSource stop)
    {
        await ready;
        try { await stop.CancelAsync(); }
        catch (Exception error) { SessionTitleFailure(error); }
    }
    private static async Task DrainPublicationAfter(Task ready, SessionPublicationOwner owner)
    {
        await ready;
        try { await owner.Cancellation; await Task.WhenAll(owner.Work); }
        finally { owner.Items.Clear(); owner.Stop.Dispose(); }
    }
    private async Task JoinSessionPublications()
    {
        Task[] drains;
        await _socketSend.WaitAsync();
        try { _ = EndSessionPublications(); drains = _sessionTitleDrains.ToArray(); }
        finally { _socketSend.Release(); }
        await Task.WhenAll(drains); _sessionTitleSerial.Dispose();
    }
    private void CancelResolution(SessionPublicationOwner owner, SessionPublication item)
    {
        if (item.Resolution is not { } resolution) return;
        item.Resolution = null; resolution.Revoked = true;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resolution.Cancellation = CancelPublicationAfter(ready.Task, resolution.Stop);
        owner.Work.Add(resolution.Cancellation); ready.SetResult();
    }
    private void QueueResolution(SessionPublicationOwner owner, SessionPublication item,
        OwnerConnectionBinding? expectedOwner = null, EngineMirror.LiveMirrorBinding? live = null)
    {
        var resolution = new SessionTitleResolution(owner, item.Reference!, item.Ordinal, expectedOwner, live);
        item.Resolution = resolution; item.AttemptedOrdinal = item.Ordinal;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Work.RemoveAll(task => task.IsCompletedSuccessfully);
        resolution.Work = ResolveSessionTitleAfter(ready.Task, owner, item, resolution);
        owner.Work.Add(resolution.Work);
        ready.SetResult();
    }

    private void ReceiveSessionPublication(JsonElement frame, ClientWebSocket socket,
        OwnerConnectionBinding? expectedOwner = null, EngineMirror.LiveMirrorBinding? live = null)
    {
        try { ReceiveSessionPublicationCore(frame, socket, expectedOwner, live); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new IOException("invalid session title publication", error); }
    }
    private void ReceiveSessionPublicationCore(JsonElement frame, ClientWebSocket socket,
        OwnerConnectionBinding? expectedOwner, EngineMirror.LiveMirrorBinding? live)
    {
        var owner = _sessionPublication;
        if (owner is null || owner.Revoked || !ReferenceEquals(owner.Socket, socket)) throw new IOException("session publication owner changed");
        if (frame.GetProperty("t").GetString() == "state")
        {
            owner.Negotiated = frame.TryGetProperty("sessionTitleParts", out var version) && version.GetInt32() == 1;
            if (frame.TryGetProperty("sessionTitleParts", out version) && version.GetInt32() != 1) throw new IOException("invalid sessions title negotiation");
        }
        if (frame.TryGetProperty("items", out var items))
            foreach (var entry in items.EnumerateArray()) ReceiveSessionItem(owner, entry.GetProperty("item"), expectedOwner, live);
        else if (frame.TryGetProperty("item", out var item)) ReceiveSessionItem(owner, item, expectedOwner, live);
    }
    private void ReceiveSessionItem(SessionPublicationOwner owner, JsonElement json,
        OwnerConnectionBinding? expectedOwner, EngineMirror.LiveMirrorBinding? live)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new IOException("invalid session publication");
        var id = json.GetProperty("sessionId").GetString() ?? throw new IOException("invalid session identity");
        var notice = json.TryGetProperty("sessionTitleRefVersion", out var version) || json.TryGetProperty("titleReference", out _);
        LegacySessionTitleRef? reference = null;
        SessionItem? inline = null;
        if (notice)
        {
            if (!owner.Negotiated || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 || json.TryGetProperty("title", out _))
                throw new IOException("invalid session title notice");
            reference = json.GetProperty("titleReference").Deserialize(SessionsJson.Default.LegacySessionTitleRef) ?? throw new IOException("invalid title reference");
            if (reference.SessionId != id || reference.StoredMachine != OptionalText(json, "machine") ||
                reference.ParentSessionId != OptionalText(json, "parentSessionId") || reference.TitleSource != OptionalText(json, "titleSource") ||
                reference.UpdatedAt != json.GetProperty("updatedAt").GetInt64() || reference.Utf8Bytes < 0 ||
                reference.Sha256 is not { Length: 64 } || reference.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new IOException("title reference metadata differs");
        }
        else inline = json.Deserialize(SessionsJson.Default.SessionItem) ?? throw new IOException("invalid inline session");
        if (_sessionStore.Get(id)?.Machine != _name)
        {
            if (owner.Items.Remove(id, out var obsolete)) CancelResolution(owner, obsolete);
            return;
        }
        if (!owner.Items.TryGetValue(id, out var item)) owner.Items.Add(id, item = new(id));
        item.Ordinal = checked(item.Ordinal + 1);
        if (inline is not null)
        {
            CancelResolution(owner, item); item.Notice = null; item.Reference = null;
            ApplySession(inline); return;
        }
        // A global push may arrive between seeding and the keyed state. Preserve an active pin,
        // and never treat a legacy task as though it captured the new caller's owner/generation.
        if (expectedOwner is null && item.Resolution?.ExpectedOwner is { } pinned)
        {
            expectedOwner = pinned; live = item.Resolution.Live;
        }
        if (item.Resolution is { } current && SameTitleKey(item.Reference!, reference!) &&
            (expectedOwner is null || ReferenceEquals(current.ExpectedOwner, expectedOwner) && ReferenceEquals(current.Live, live)))
        {
            item.Reference = reference; item.Notice = json.Clone(); return;
        }
        CancelResolution(owner, item); item.Reference = reference; item.Notice = json.Clone(); QueueResolution(owner, item, expectedOwner, live);
    }
    private void ApplySession(SessionItem item)
    {
        if (_sessionStore.ApplyRemote(item, _name) && item.ActiveRunId is { } runId) _follower.Watch(item.SessionId, runId);
    }
    private static bool SameTitleKey(LegacySessionTitleRef left, LegacySessionTitleRef right) => left.SessionId == right.SessionId &&
        left.PublisherEngineMachine == right.PublisherEngineMachine && left.StoredMachine == right.StoredMachine &&
        left.ParentSessionId == right.ParentSessionId && left.TitleSource == right.TitleSource && left.Utf8Bytes == right.Utf8Bytes && left.Sha256 == right.Sha256;
    private static string? OptionalText(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private void SessionTitleFailure(Exception error)
    {
        try { _log.WriteLine($"remote {_name}: session title pending ({error.GetType().Name}: {error.Message})"); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
