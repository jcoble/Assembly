using Assembly.Infrastructure.Engine;
using Assembly.Application.Common.Agents;
using System.Security.Cryptography;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Machines;
using Assembly.Infrastructure.Machines;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Infrastructure.Remote;

/// <summary>
/// A machine whose agent runs elsewhere, reached at <paramref name="address"/> with its own token: a Assembly.Remote
/// serve on the same engine, which converts and stores its own rows. Creating, sending, stopping, tool details and
/// attachments are its routes; its rows are copied into the desktop's store (<see cref="RemoteFollower"/>), which the
/// webview reads like any other session's.
/// </summary>
internal sealed partial class RemoteMachine : IMachine, IAsyncDisposable
{
    private readonly string _name;
    private readonly LiveHub _hub;
    private readonly SessionStore _sessionStore;
    private readonly RemoteFollower _follower;
    private readonly Action<string> _turnEnded;
    private readonly MachineStatus _status;
    private readonly string _connectionIdentity;
    private readonly string _instanceIdentity = Guid.NewGuid().ToString("N");

    /// <param name="turnEnded">Told when a copied turn ends (a helper's row goes idle).</param>
    public RemoteMachine(string name, Uri address, string token, LiveHub hub, SessionStore sessionStore, TextWriter log, MachineStatus status,
        Action<string>? turnEnded = null, Assembly.Application.Common.Topics.ITopics? topics = null, TimeProvider? time = null, string? connectionIdentity = null)
    {
        _connectionIdentity = connectionIdentity ?? "";
        Terminals = new RemoteTerminalSocket(name, address, token, topics ?? new Topics.Topics());
        _address = address;
        _token = token;
        _time = time ?? TimeProvider.System;
        _log = log;
        _turnEnded = turnEnded ?? (_ => { });
        _name = name;
        _hub = hub;
        _sessionStore = sessionStore;
        _status = status;
        _follower = new RemoteFollower(hub, RowsAsync, ReadRowsAsync, ProbeAsync, RefreshAsync, AddSubagentSession, log);
    }

    public string? Name => _name;
    internal RemoteTerminalSocket Terminals { get; }

    internal string[] FollowedSessions => _follower.FollowedSessions;

    // A subagent's session, made on the remote by its parent's session, is known here from its parent's row.
    private void AddSubagentSession(string parentSessionId, StoredRow row)
    {
        if (row.Payload.GetProperty("sessionId").GetString() is not { } childId || _sessionStore.Get(childId) is not null
            || _sessionStore.Get(parentSessionId) is not { } parent)
            return;
        var agent = row.Payload.TryGetProperty("agent", out var named) && named.GetString() is { } helperAgent ? helperAgent : parent.Agent;
        _sessionStore.Add(childId, agent, parent.RepoUrl, _name, parentSessionId,
            helper: row.Payload.TryGetProperty("helper", out var helper) && helper.ValueKind == JsonValueKind.True);
        if (row.Payload.GetProperty("title").GetString() is { } title)
            _sessionStore.SetTitle(childId, title, "prompt");
    }

    public async Task<IReadOnlyList<ConfigTarget>> AgentsAsync(CancellationToken cancellationToken)
    {
        var owner = await CaptureOwnerReadAsync(null, cancellationToken);
        var listed = await CallAsync("machines", "agents", JsonSerializer.SerializeToElement(new MachinesFeature.AgentsArgs(),
            SessionsJson.Default.AgentsArgs), false, cancellationToken, owner.Identity, owner.Socket);
        var agents = new List<ConfigTarget>();
        if (listed.ValueKind != JsonValueKind.Object || !listed.TryGetProperty("agents", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new CallFailed("incompatible backend");
        foreach (var item in items.EnumerateArray())
        {
            string? Field(string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var id = Field("id"); var name = Field("name"); var target = Field("targetId");
            var account = Field("accountContext"); var providerTarget = Field("providerTargetId");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || target is null || target.Length != 64
                || target.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || !AccountContext.IsValid(account) || providerTarget != id + "/" + account)
                throw new CallFailed("incompatible backend");
            agents.Add(new(id, name, TargetPrefix + target, account, providerTarget));
        }
        await _socketSend.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(RequireEngine(owner.Identity), owner.Socket))
                throw Unreachable("machine socket changed", $"{_name}: owning engine connection changed");
            return agents;
        }
        finally { _socketSend.Release(); }
    }

    internal async Task<SessionsFeature.Created> CreateBoundSessionAsync(SessionsFeature.CreateArgs request,
        SessionsFeature.CreateAtRootArgs? descriptor, OwnerConnectionBinding owner, ClientWebSocket socket,
        EngineMirror.LiveMirrorBinding? live, CancellationToken ct,
        Action<Assembly.Application.Common.Database.IWriteScope, SessionsFeature.Created>? accepted = null)
    {
        var initial = request.InitialConfig is null ? null
            : request.InitialConfig with { TargetId = UnwrapTarget(request.InitialConfig.TargetId) };
        var normalized = request with
        {
            Machine = null,
            InitialConfig = initial,
            ExpectedSource = request.ExpectedSource is null ? null : new(owner.Machine, 0, null)
        };
        var args = descriptor is null
            ? JsonSerializer.SerializeToElement(normalized, SessionsJson.Default.CreateArgs)
            : JsonSerializer.SerializeToElement(descriptor with { Create = normalized }, SessionsJson.Default.CreateAtRootArgs);
        var reply = await CallAsync("sessions", descriptor is null ? "create" : "create_at_root", args, false, ct,
            owner.Machine, socket, expectedOwner: owner, expectedMirror: live);
        var created = reply.Deserialize(SessionsJson.Default.Created);
        if (created is null || string.IsNullOrWhiteSpace(created.SessionId))
            throw new CallFailed("The owner returned no accepted session.", "configuration_unavailable");
        await AcceptCreatedSessionAsync(created, owner, socket, live, ct,
            accepted is null ? null : scope => accepted(scope, created));
        return created;
    }

    internal async Task<SessionsFeature.ByClientReply> ReadBoundCreatedSessionAsync(string clientId,
        bool qualified, OwnerConnectionBinding owner, ClientWebSocket socket,
        EngineMirror.LiveMirrorBinding? live, CancellationToken ct,
        Action<Assembly.Application.Common.Database.IWriteScope, SessionsFeature.ByClientReply>? accepted = null)
    {
        var args = new SessionsFeature.ByClientArgs(clientId,
            ExpectedSource: qualified ? new(owner.Machine, 0, null) : null);
        var reply = await CallAsync("sessions", "byClient", JsonSerializer.SerializeToElement(args, SessionsJson.Default.ByClientArgs),
            false, ct, owner.Machine, socket, expectedOwner: owner, expectedMirror: live);
        var known = reply.Deserialize(SessionsJson.Default.ByClientReply)
            ?? throw new CallFailed("The owner returned no creation correlation.", "configuration_unavailable");
        if (known.SessionId is not null)
            await AcceptCreatedSessionAsync(new(known.SessionId, known.CheckoutOperationId, known.ProjectId, known.AcceptedRoot, known.Facts),
                owner, socket, live, ct, accepted is null ? null : scope => accepted(scope, known));
        return known;
    }

    private async Task AcceptCreatedSessionAsync(SessionsFeature.Created created, OwnerConnectionBinding owner,
        ClientWebSocket socket, EngineMirror.LiveMirrorBinding? live, CancellationToken ct,
        Action<Assembly.Application.Common.Database.IWriteScope>? accepted)
    {
        // Older peers may acknowledge acceptance without certifying copied metadata. Never invent it.
        if (created.Facts is not { } facts) return;
        if (facts.Machine != owner.Machine) throw new CallFailed("The accepted session owner changed.", "source_changed");
        await _socketSend.WaitAsync(ct);
        try
        {
            void Validate(Assembly.Application.Common.Database.IWriteScope _)
            {
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(RequireOwnerBinding(owner), socket))
                    throw new CallFailed("The accepting owner connection changed.", "source_changed");
                RequireLiveMirror(owner, live);
            }
            _sessionStore.AcceptCopiedCreation(created.SessionId, facts, _name, created.ProjectId, Validate, accepted);
        }
        finally { _socketSend.Release(); }
        await RefreshCreatedSessionTitleAsync(created.SessionId, owner, socket, live, ct);
    }

    private sealed record CreatedTitleSubscription(string SessionId, ClientWebSocket Socket, OwnerConnectionBinding Owner,
        EngineMirror.LiveMirrorBinding? Live, TaskCompletionSource<Task?> State);
    private readonly Dictionary<long, CreatedTitleSubscription> _createdTitles = [];

    private async Task RefreshCreatedSessionTitleAsync(string sessionId, OwnerConnectionBinding owner,
        ClientWebSocket socket, EngineMirror.LiveMirrorBinding? live, CancellationToken cancellation)
    {
        using var stop = live is null ? CancellationTokenSource.CreateLinkedTokenSource(cancellation, owner.Invalidated)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellation, owner.Invalidated, live.Invalidated);
        var ct = stop.Token;
        var id = Interlocked.Increment(ref _nextSub);
        var state = new TaskCompletionSource<Task?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _socketSend.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(RequireOwnerBinding(owner), socket)) throw new CallFailed("The accepting owner connection changed.", "source_changed");
            RequireLiveMirror(owner, live);
            _createdTitles.Add(id, new(sessionId, socket, owner, live, state));
            await SendFrameAsync(socket, $"{{\"t\":\"sub\",\"id\":{id},\"topic\":\"sessions\",\"key\":{JsonSerializer.Serialize(sessionId, RowJsonContext.Rows.String)},\"sessionTitleParts\":1}}", ct);
        }
        catch { _createdTitles.Remove(id); throw; }
        finally { _socketSend.Release(); }
        Task? resolution = null;
        try
        {
            resolution = await state.Task.WaitAsync(ct);
            if (resolution is not null) await resolution.WaitAsync(ct);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is IOException or CallFailed or OperationCanceledException)
        {
            // Acceptance remains committed. Keep metadata pending rather than resending creation or fabricating a title.
            await _socketSend.WaitAsync(cancellation);
            try { RequireOwnerBinding(owner); RequireLiveMirror(owner, live); }
            finally { _socketSend.Release(); }
            SessionTitleFailure(error);
        }
        finally
        {
            Task? cleanup = null;
            await _socketSend.WaitAsync(CancellationToken.None);
            try
            {
                _createdTitles.Remove(id);
                if (_sessionPublication is { } publication && publication.Items.TryGetValue(sessionId, out var item) &&
                    item.Resolution is { } pending && ReferenceEquals(pending.ExpectedOwner, owner) &&
                    ReferenceEquals(pending.Live, live) && !pending.Work.IsCompleted)
                {
                    cleanup = pending.Work;
                    CancelResolution(publication, item);
                }
                if (_socket is { State: WebSocketState.Open } current && ReferenceEquals(current, socket) && !owner.IsInvalidated)
                    try { await SendFrameAsync(socket, $"{{\"t\":\"unsub\",\"id\":{id}}}", _socketStop.Token); }
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException) { socket.Abort(); }
            }
            finally { _socketSend.Release(); }
            if (cleanup is not null) await cleanup;
            if (resolution is not null && !ReferenceEquals(resolution, cleanup)) await resolution;
        }
    }

    public async Task<bool> StopAsync(SessionInfo session, string runId, CancellationToken cancellationToken)
    {
        try { return (await CallAsync("send", "stop", new SendFeature.StopArgs(runId), cancellationToken)).GetProperty("ok").GetBoolean(); }
        catch (CallFailed error) when (error.Message == "that run isn't running") { return false; }
    }

    // From the copied rows, so it agrees with what the webview has.
    public string? ActiveRun(SessionInfo session) => _hub.OpenRun(session.SessionId);

    public async Task<StoredToolCall?> ToolCallAsync(SessionInfo session, string itemId, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.SerializeToElement(new RowsFeature.RawToolArgs(session.SessionId, itemId,
            RowsFeature.ToolCap, RowsFeature.ToolCap), RowJsonContext.Rows.RawToolArgs);
        try
        {
            var value = await CallAsync("rows", "toolRaw", args, bulk: true, cancellationToken);
            return value.Deserialize(RowJsonContext.Rows.StoredToolCall);
        }
        catch (CallFailed error) when (error.Message == "unknown tool") { return null; }
    }

    public Task<string> SaveAttachmentAsync(SessionInfo session, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        SaveAttachmentAsync(session, type, bytes, cancellationToken, null);

    internal async Task<string> SaveAttachmentAsync(SessionInfo session, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken,
        Assembly.Infrastructure.Engine.EngineMirror.LiveMirrorBinding? live)
    {
        (string Identity, ClientWebSocket Socket) owner;
        if (live is null) owner = await CaptureOwnerReadAsync(null, cancellationToken);
        else
        {
            await _socketSend.WaitAsync(cancellationToken);
            try
            {
                RequireLiveMirror(live.OwnerBinding, live);
                owner = (live.Machine, RequireOwnerBinding(live.OwnerBinding));
            }
            finally { _socketSend.Release(); }
        }
        await _bulkCall.WaitAsync(cancellationToken);
        ClientWebSocket? original = null;
        string? upload = null;
        var completed = false;
        try
        {
            var begun = await BulkCallHeldAsync("attachments", "begin", JsonSerializer.SerializeToElement(
                new AttachmentsFeature.BeginArgs(session.SessionId, type.ContentType, bytes.Length, live is null ? null : new(live.Machine, 0, null)), AttachmentsJson.Default.BeginArgs),
                cancellationToken, owner.Identity, owner.Socket, live?.OwnerBinding, live);
            original = _bulkSocket;
            upload = begun.GetProperty("upload").GetString()!;
            for (var offset = 0; offset < bytes.Length; offset += AttachmentsFeature.ChunkBytes)
            {
                var chunk = bytes.Slice(offset, Math.Min(AttachmentsFeature.ChunkBytes, bytes.Length - offset)).ToArray();
                RequireOriginal();
                await BulkCallHeldAsync("attachments", "chunk", JsonSerializer.SerializeToElement(
                    new AttachmentsFeature.ChunkArgs(upload, offset, chunk), AttachmentsJson.Default.ChunkArgs),
                    cancellationToken, owner.Identity, owner.Socket, live?.OwnerBinding, live);
            }
            RequireOriginal();
            var saved = await BulkCallHeldAsync("attachments", "commit", JsonSerializer.SerializeToElement(
                new AttachmentsFeature.CommitArgs(upload), AttachmentsJson.Default.CommitArgs),
                cancellationToken, owner.Identity, owner.Socket, live?.OwnerBinding, live);
            var path = saved.GetProperty("path").GetString()!;
            completed = true;
            return path;
        }
        finally
        {
            try
            {
                if (!completed && upload is not null && SameOriginal())
                {
                    try
                    {
                        using var deadline = new CancellationTokenSource(Assembly.Infrastructure.Socket.SocketDispatcher.Limit, _time);
                        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _socketStop.Token);
                        await BulkCallHeldAsync("attachments", "drop", JsonSerializer.SerializeToElement(
                            new AttachmentsFeature.DropArgs(upload), AttachmentsJson.Default.DropArgs),
                            cleanup.Token, owner.Identity, owner.Socket, live?.OwnerBinding, live);
                    }
                    catch (Exception)
                    {
                        // Preserve the upload's error; closing its original connection owns staging cleanup.
                        try { original?.Abort(); } catch (ObjectDisposedException) { }
                    }
                }
            }
            finally { _bulkCall.Release(); }
        }

        bool SameOriginal() => original is { State: WebSocketState.Open } && ReferenceEquals(_bulkSocket, original);
        void RequireOriginal()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SameOriginal()) throw Unreachable("machine socket changed", $"{_name}: upload connection changed");
        }
    }

    public Task<AttachmentPiece> ReadAttachmentAsync(SessionInfo session, string name, long offset, CancellationToken cancellationToken) =>
        ReadAttachmentAsync(session, name, offset, cancellationToken, null);

    internal async Task<AttachmentPiece> ReadAttachmentAsync(SessionInfo session, string name, long offset, CancellationToken cancellationToken,
        Assembly.Infrastructure.Engine.EngineMirror.LiveMirrorBinding? live)
    {
        var type = string.IsNullOrEmpty(name) ? null : AttachmentStore.TypeOfName(name);
        if (type is null) throw new CallFailed("not an attachment name");
        if (offset < 0) throw new CallFailed("the offset is outside the attachment");
        var args = JsonSerializer.SerializeToElement(new AttachmentsFeature.ReadArgs(session.SessionId, name, offset, live is null ? null : new(live.Machine, 0, null)),
            AttachmentsJson.Default.ReadArgs);
        JsonElement value;
        try { value = await CallAsync("attachments", "read", args, bulk: true, cancellationToken, live?.Machine, expectedOwner: live?.OwnerBinding, expectedMirror: live); }
        catch (Exception error) when (error is WebSocketException or IOException)
        {
            throw Unreachable("machine socket closed", $"{_name}: machine socket closed", error);
        }
        var piece = value.Deserialize(AttachmentsJson.Default.AttachmentPiece)
            ?? throw new CallFailed("invalid attachment reply");
        if (piece.Size is < 1 or > AttachmentStore.MaxBytes || offset > piece.Size || piece.Data is null ||
            piece.Data.Length != Math.Min(AttachmentsFeature.ChunkBytes, piece.Size - offset) || piece.Mime != type.ContentType)
            throw new CallFailed("invalid attachment reply");
        return piece;
    }

    public async Task<SessionConfig> ConfigAsync(SessionInfo session, CancellationToken cancellationToken) =>
        ReadConfig(await CallAsync("config", "get", new ConfigFeature.GetArgs(session.SessionId), cancellationToken));

    public async Task<SessionConfig> SetConfigAsync(SessionInfo session, string configId, string value, CancellationToken cancellationToken, string? targetId = null)
    {
        var args = new ConfigFeature.SetArgs(session.SessionId, configId, value, targetId is null ? null : UnwrapTarget(targetId));
        return ReadConfig(targetId is null
            ? await CallAsync("config", "set", args, cancellationToken)
            : await ConfiguredCallAsync("config", "set", JsonSerializer.SerializeToElement(args, SessionsJson.Default.SetArgs), cancellationToken));
    }

    public async Task<ComposerCatalogue> DiscoverConfigAsync(string agent, string targetId, string cwd, CancellationToken ct)
    {
        var args = new ConfigFeature.DiscoverArgs(agent, UnwrapTarget(targetId), cwd);
        var result = await ConfiguredCallAsync("config", "discover", JsonSerializer.SerializeToElement(args, SessionsJson.Default.DiscoverArgs), ct);
        if (!result.TryGetProperty("modelOptions", out var models) || models.ValueKind != JsonValueKind.Object
            || models.EnumerateObject().Any(model => model.Value.ValueKind != JsonValueKind.Array))
            throw new CallFailed("incompatible backend");
        return new(ReadConfig(result), models.EnumerateObject().ToDictionary(model => model.Name, model => model.Value.GetRawText()));
    }

    public async Task<SessionConfig> WarmConfigAsync(SessionInfo session, string targetId, CancellationToken ct)
    {
        var args = new ConfigFeature.WarmArgs(session.SessionId, UnwrapTarget(targetId));
        return ReadConfig(await ConfiguredCallAsync("config", "warm", JsonSerializer.SerializeToElement(args, SessionsJson.Default.WarmArgs), ct));
    }

    private async Task<JsonElement> ConfiguredCallAsync(string feature, string method, JsonElement args, CancellationToken ct)
    {
        var owner = await CaptureOwnerReadAsync(null, ct);
        return await CallAsync(feature, method, args, false, ct, owner.Identity, owner.Socket);
    }

    private string TargetPrefix => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_address.AbsoluteUri + "\0" + _token + "\0" + _connectionIdentity + "\0" + _instanceIdentity))) + ":";

    private string UnwrapTarget(string target)
    {
        if (string.IsNullOrEmpty(target) || !target.StartsWith(TargetPrefix, StringComparison.Ordinal) || target.Length == TargetPrefix.Length)
            throw new CallFailed("The selected remote configuration changed. Select it again.");
        return target[TargetPrefix.Length..];
    }

    internal async Task<bool> AnswerOwnerNeedAsync(SessionInfo session, string askId, JsonElement answer, CancellationToken cancellationToken)
    {
        try { return (await CallAsync("send", "answer", new SendFeature.AnswerArgs(session.SessionId, askId, answer), cancellationToken)).GetProperty("ok").GetBoolean(); }
        // SendFeature.cs rejects an expired ask with this failed message.
        catch (CallFailed error) when (error.Message == "that ask isn't open, or the answer doesn't fit it") { return false; }
    }

    public async Task<bool> StopTaskAsync(SessionInfo session, string taskId, CancellationToken cancellationToken)
    {
        try { return (await CallAsync("send", "stopTask", new SendFeature.StopTaskArgs(session.SessionId, taskId), cancellationToken)).GetProperty("ok").GetBoolean(); }
        catch (CallFailed error) when (error.Message == "that task isn't running, or can't be stopped") { return false; }
    }

    private static SessionConfig ReadConfig(JsonElement config) =>
        new(config.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array ? options.GetRawText() : null,
            SessionStore.ReadChoices(config.GetProperty("choices").GetRawText()),
            config.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array ? commands.GetRawText() : null);

    public void Watch(SessionInfo session) => _follower.Watch(session.SessionId);

    /// <summary>A turn delivered through the Board socket still needs its rows copied here.</summary>
    internal void WatchRun(string sessionId, string runId) => _follower.Watch(sessionId, runId);

    /// <summary>Follows the remote sessions whose copied rows show a turn still running, as after a desktop restart mid-turn.</summary>
    public void WatchRunning(IEnumerable<string> sessionIds, IEnumerable<string> helpersShownRunning)
    {
        _follower.ShownRunning(helpersShownRunning);
        foreach (var sessionId in sessionIds.Concat(helpersShownRunning).Distinct())
            _follower.Watch(sessionId);
    }

    /// <summary>Opens the machine socket and checks the welcome before marking it reachable.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        await ConnectSocketAsync(cancellationToken);
        await _socketSend.WaitAsync(cancellationToken);
        try { _socketRun ??= Task.Run(SocketLoopAsync, CancellationToken.None); }
        finally { _socketSend.Release(); }
        _status.Reachable(_name);
        _follower.Resume();
    }

    private Task RefreshAsync(string sessionId)
    {
        _turnEnded(sessionId);
        return Task.CompletedTask;
    }

    // Recorded before it's thrown.
    private MachineUnreachable Unreachable(string reason, string message, Exception? inner = null)
    {
        _status.Unreachable(_name, reason);
        return new MachineUnreachable(_name, message, inner);
    }

    public async ValueTask DisposeAsync()
    {
        var relayLeases = StopRelayLeases();
        Task[] gitLifetimes, runtimeLifetimes;
        await _socketSend.WaitAsync();
        try
        {
            _ownerStopping = true; InvalidateEngine();
            gitLifetimes = _gitSubscriptions.Values.Select(subscription => subscription.Closed.Task).Concat(_gitCallDrains).ToArray();
            runtimeLifetimes = _runtimeSubscriptions.Values.Select(subscription => subscription.Closed.Task).ToArray();
        }
        finally { _socketSend.Release(); }
        await Terminals.DisposeAsync();
        await _socketStop.CancelAsync();
        await _follower.DisposeAsync();
        if (_socketRun is not null) await _socketRun;
        await JoinSessionPublications();
        await Task.WhenAll(relayLeases);
        await Task.WhenAll(gitLifetimes);
        await Task.WhenAll(runtimeLifetimes);
        _bulkSocket?.Dispose();
        await Task.WhenAll(_ownerCancellations);
        _socketStop.Dispose();
        _socketSend.Dispose();
        _bulkCall.Dispose();
    }
}
