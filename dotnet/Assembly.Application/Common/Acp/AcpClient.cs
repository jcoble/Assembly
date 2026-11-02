using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;

namespace Assembly.Application.Common.Acp;

/// <summary>One ACP v2 connection. Adapter differences stay outside this client.</summary>
public sealed class AcpClient : IJsonRpcTarget, IAsyncDisposable
{
    private readonly IPermissionPolicy _permissions;
    private readonly ConcurrentDictionary<string, IPermissionPolicy> _sessionPolicies = new();
    private sealed record NativeParent(string Id, bool Conflicted = false);
    private readonly ConcurrentDictionary<string, NativeParent> _nativeParents = new();
    private readonly ConcurrentDictionary<string, Foreground> _foreground = new();
    private readonly Channel<SessionNotification> _updates =
        Channel.CreateUnbounded<SessionNotification>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly JsonRpcConnection _rpc;
    private sealed class ResumeState { public SessionNotification? Snapshot; }
    private readonly ConcurrentDictionary<string, ResumeState> _resuming = new();
    public event Action<string>? SessionResumed;
    private readonly BackgroundWork _prompts = new(TimeProvider.System, Console.Error);

    public AcpClient(Stream fromAgent, Stream toAgent, IPermissionPolicy permissions, TranscriptLog transcript)
    {
        _permissions = permissions;
        _rpc = new JsonRpcConnection(fromAgent, toAgent, this, transcript);
    }

    public ChannelReader<SessionNotification> Updates => _updates.Reader;

    public void SetSessionPolicy(string sessionId, IPermissionPolicy? policy)
    {
        if (policy is not null)
        {
            _nativeParents.TryRemove(sessionId, out _); // an independently installed root is never a child alias
            _sessionPolicies[sessionId] = policy;
        }
        else
        {
            _sessionPolicies.TryRemove(sessionId, out _);
            ForgetNativeChildren(sessionId);
        }
    }

    public async Task<InitializeResponse> InitializeAsync(Implementation clientInfo, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(AcpMethods.Initialize,
            new InitializeRequest(AcpProtocol.Version, clientInfo, ClientCapabilities.Assembly),
            AcpJsonContext.Default.InitializeRequest, AcpJsonContext.Default.InitializeResponse, cancellationToken);
        if (response.ProtocolVersion != AcpProtocol.Version)
            throw new JsonRpcException(-32022, $"ACP version {response.ProtocolVersion} is unsupported; Assembly requires version 2.");
        return response;
    }

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
        RequestAsync(AcpMethods.SessionNew, request, AcpJsonContext.Default.NewSessionRequest,
            AcpJsonContext.Default.NewSessionResponse, cancellationToken);

    public async Task<ResumeSessionResponse> ResumeSessionAsync(ResumeSessionRequest request, CancellationToken cancellationToken)
    {
        if (!_resuming.TryAdd(request.SessionId, new ResumeState()))
            throw new InvalidOperationException("This session is already resuming.");
        try
        {
            return await RequestAsync(AcpMethods.SessionResume, request, AcpJsonContext.Default.ResumeSessionRequest,
                AcpJsonContext.Default.ResumeSessionResponse, cancellationToken, () =>
                {
                    _resuming.TryRemove(request.SessionId, out var resumed);
                    SessionResumed?.Invoke(request.SessionId);
                    if (resumed?.Snapshot is { } snapshot)
                    {
                        For(request.SessionId).Read(snapshot, () => _updates.Writer.TryWrite(snapshot));
                    }
                });
        }
        finally { _resuming.TryRemove(request.SessionId, out _); }
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await RequestAsync(AcpMethods.SessionClose, new CloseSessionRequest(sessionId),
                AcpJsonContext.Default.CloseSessionRequest, AcpJsonContext.Default.CloseSessionResponse, cancellationToken);
        }
        finally
        {
            if (_foreground.TryRemove(sessionId, out var foreground)) foreground.Dispose();
            SetSessionPolicy(sessionId, null);
        }
    }

    public Task<SetConfigOptionResponse> SetConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken) =>
        RequestAsync(AcpMethods.SessionSetConfigOption, new SetConfigOptionRequest(sessionId, configId, value),
            AcpJsonContext.Default.SetConfigOptionRequest, AcpJsonContext.Default.SetConfigOptionResponse, cancellationToken);

    public PromptRun PromptAsync(string sessionId, IReadOnlyList<TextBlock> prompt, CancellationToken cancellationToken)
    {
        var foreground = For(sessionId);
        var pending = foreground.Begin();
        _prompts.Start("ACP prompt " + sessionId, SendPromptAsync);
        return new PromptRun(pending.Inserted.Task, pending.Ended.Task);

        async Task SendPromptAsync(CancellationToken closing)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing);
            try
            {
                var response = await RequestAsync(AcpMethods.SessionPrompt, new PromptRequest(sessionId, prompt),
                    AcpJsonContext.Default.PromptRequest, AcpJsonContext.Default.PromptResponse, lifetime.Token);
                if (string.IsNullOrEmpty(response.MessageId))
                    throw new JsonRpcException(JsonRpcException.InternalError, "session/prompt returned no messageId.");
                foreground.Inserted(pending, response.MessageId);
                await pending.Ended.Task.WaitAsync(lifetime.Token);
            }
            catch (Exception error)
            {
                foreground.Failed(pending, error);
            }
        }
    }

    public async Task<SteerOutcome> SteerAsync(string sessionId, IReadOnlyList<TextBlock> prompt, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(AcpMethods.SessionSteering,
            new SteerRequest(sessionId, prompt, new SteerMeta(new SteerOptions("promptRequired"))),
            AcpJsonContext.Default.SteerRequest, AcpJsonContext.Default.SteerResponse, cancellationToken);
        return response.Outcome switch
        {
            "injected" => SteerOutcome.Injected,
            "promptRequired" => SteerOutcome.PromptRequired,
            _ => throw new JsonRpcException(JsonRpcException.InternalError,
                $"Unexpected steering outcome with idleBehavior promptRequired: {response.Outcome}.")
        };
    }

    public async Task CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_nativeParents.TryGetValue(sessionId, out var parent) && !parent.Conflicted
            && _sessionPolicies.TryGetValue(parent.Id, out var policy))
            policy.Cancelling(sessionId);
        var foreground = For(sessionId);
        var waiting = foreground.Cancelling();
        try
        {
            await _rpc.NotifyAsync(AcpMethods.SessionCancel,
                JsonSerializer.SerializeToElement(new CancelNotification(sessionId), AcpJsonContext.Default.CancelNotification), cancellationToken);
            await waiting.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            foreground.RemoveCancel(waiting);
        }
    }

    Task<JsonElement> IJsonRpcTarget.OnRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        OnRequestAsync(method, parameters, null, cancellationToken);

    Task<JsonElement> IJsonRpcTarget.OnRequestAsync(string method, JsonElement parameters, JsonRpcRequestContext request,
        CancellationToken cancellationToken) => OnRequestAsync(method, parameters, request, cancellationToken);

    private async Task<JsonElement> OnRequestAsync(string method, JsonElement parameters, JsonRpcRequestContext? context,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case AcpMethods.SessionRequestPermission:
                var request = parameters.Deserialize(AcpJsonContext.Default.RequestPermissionRequest)
                    ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing permission request.");
                var outcome = await AskAsync(request.SessionId, PermissionOutcome.Cancelled,
                    (policy, token) => request.ToolTitle == "mcp__assembly__board_update"
                        && request.Options.FirstOrDefault(option => option.Kind == "allow_once") is { } allow
                        ? Task.FromResult(PermissionOutcome.Selected(allow.OptionId))
                        : context is null ? policy.DecideAsync(request, token) : policy.DecideAsync(request, context, token),
                    cancellationToken, context, method, value => JsonSerializer.SerializeToElement(value, AcpJsonContext.Default.PermissionOutcome));
                return JsonSerializer.SerializeToElement(new RequestPermissionResponse(outcome), AcpJsonContext.Default.RequestPermissionResponse);
            case AcpMethods.ElicitationCreate:
                var question = parameters.Deserialize(AcpJsonContext.Default.ElicitationRequest)
                    ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing elicitation request.");
                var session = !string.IsNullOrEmpty(question.SessionId);
                var requestId = question.RequestId is { ValueKind: JsonValueKind.String or JsonValueKind.Number };
                if (session == requestId)
                    throw new JsonRpcException(JsonRpcException.InvalidParams, "Elicitation requires exactly one of sessionId or requestId.");
                var answer = question.Mode != "form" || !session
                    ? ElicitationResponse.Cancel
                    : await AskAsync(question.SessionId!, ElicitationResponse.Cancel,
                        (policy, token) => context is null ? policy.ElicitAsync(question, token) : policy.ElicitAsync(question, context, token),
                        cancellationToken, context, method, value => JsonSerializer.SerializeToElement(value, AcpJsonContext.Default.ElicitationResponse));
                return JsonSerializer.SerializeToElement(answer, AcpJsonContext.Default.ElicitationResponse);
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {method}");
        }
    }

    private async Task<T> AskAsync<T>(string sessionId, T cancelled,
        Func<IPermissionPolicy, CancellationToken, Task<T>> ask, CancellationToken connectionClosing,
        JsonRpcRequestContext? context, string method, Func<T, JsonElement> serialize)
    {
        try
        {
            var owner = sessionId;
            IPermissionPolicy policy;
            if (_nativeParents.TryGetValue(sessionId, out var parent))
            {
                if (parent.Conflicted || !_sessionPolicies.TryGetValue(parent.Id, out var parentPolicy)) return cancelled;
                owner = parent.Id;
                policy = parentPolicy;
            }
            else policy = _sessionPolicies.GetValueOrDefault(sessionId) ?? _permissions;
            if (!_foreground.TryGetValue(owner, out var foreground) || !foreground.Running)
                return cancelled;
            // This selection runs inline in request arrival order. Retain both through the await.
            using var either = CancellationTokenSource.CreateLinkedTokenSource(connectionClosing, foreground.Work);
            var result = await ask(policy, either.Token);
            if (context is not null) policy.Decided(method, sessionId, context, serialize(result));
            return result;
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            return cancelled;
        }
    }

    void IJsonRpcTarget.OnNotification(string method, JsonElement parameters)
    {
        if (method != AcpMethods.SessionUpdate ||
            parameters.Deserialize(AcpJsonContext.Default.SessionNotification) is not { } update)
            return;
        if (_resuming.TryGetValue(update.SessionId, out var resuming))
        {
            // Replay text is not live traffic, but v2's required current-state snapshot remains authoritative.
            if (update.Kind == "state_update") resuming.Snapshot = update;
            return;
        }
        if (update.Kind == "state_update") For(update.SessionId).Read(update, () => PublishAccepted(update));
        else if (_foreground.TryGetValue(update.SessionId, out var foreground))
            foreground.Read(update, () => PublishAccepted(update));
        else PublishAccepted(update);
    }

    private void PublishAccepted(SessionNotification update)
    {
        if (update.Kind == "subagent_update" && _sessionPolicies.ContainsKey(update.SessionId)
            && update.Update.TryGetProperty("sessionId", out var child) && child.ValueKind == JsonValueKind.String
            && child.GetString() is { Length: > 0 } id && id != update.SessionId && !_sessionPolicies.ContainsKey(id))
            _nativeParents.AddOrUpdate(id, new NativeParent(update.SessionId), (_, known) =>
                known.Id == update.SessionId ? known : known with { Conflicted = true });
        _updates.Writer.TryWrite(update);
    }

    private void ForgetNativeChildren(string parent)
    {
        foreach (var entry in _nativeParents.Where(p => p.Value.Id == parent).ToArray())
            _nativeParents.TryRemove(entry);
    }

    void IJsonRpcTarget.OnClosed(Exception reason)
    {
        _nativeParents.Clear();
        foreach (var foreground in _foreground.Values) foreground.Close(reason);
        _updates.Writer.TryComplete();
    }

    private Foreground For(string sessionId) => _foreground.GetOrAdd(sessionId, _ => new Foreground());

    private async Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken, Action? onReply = null)
    {
        var result = await _rpc.RequestAsync(method, JsonSerializer.SerializeToElement(request, requestType), cancellationToken, onReply);
        return result.Deserialize(responseType)
            ?? throw new JsonRpcException(JsonRpcException.InternalError, $"{method} returned no result.");
    }

    public async ValueTask DisposeAsync()
    {
        await _rpc.DisposeAsync();
        await _prompts.DisposeAsync();
        foreach (var foreground in _foreground.Values) foreground.Dispose();
        _foreground.Clear();
        _nativeParents.Clear();
        _sessionPolicies.Clear();
    }
}
