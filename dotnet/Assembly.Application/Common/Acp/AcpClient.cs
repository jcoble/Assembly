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
        if (policy is not null) _sessionPolicies[sessionId] = policy;
        else _sessionPolicies.TryRemove(sessionId, out _);
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
                        For(request.SessionId).Read(snapshot);
                        _updates.Writer.TryWrite(snapshot);
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
            _sessionPolicies.TryRemove(sessionId, out _);
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

    async Task<JsonElement> IJsonRpcTarget.OnRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case AcpMethods.SessionRequestPermission:
                var request = parameters.Deserialize(AcpJsonContext.Default.RequestPermissionRequest)
                    ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing permission request.");
                var outcome = await AskAsync(request.SessionId, PermissionOutcome.Cancelled,
                    (policy, token) => request.ToolTitle == "mcp__assembly__board_update"
                        && request.Options.FirstOrDefault(option => option.Kind == "allow_once") is { } allow
                        ? Task.FromResult(PermissionOutcome.Selected(allow.OptionId)) : policy.DecideAsync(request, token), cancellationToken);
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
                        (policy, token) => policy.ElicitAsync(question, token), cancellationToken);
                return JsonSerializer.SerializeToElement(answer, AcpJsonContext.Default.ElicitationResponse);
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {method}");
        }
    }

    private async Task<T> AskAsync<T>(string sessionId, T cancelled,
        Func<IPermissionPolicy, CancellationToken, Task<T>> ask, CancellationToken connectionClosing)
    {
        try
        {
            if (!_foreground.TryGetValue(sessionId, out var foreground) || !foreground.Running)
                return cancelled;
            using var either = CancellationTokenSource.CreateLinkedTokenSource(connectionClosing, foreground.Work);
            return await ask(_sessionPolicies.GetValueOrDefault(sessionId) ?? _permissions, either.Token);
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
        if (update.Kind == "state_update") For(update.SessionId).Read(update);
        else if (_foreground.TryGetValue(update.SessionId, out var foreground)) foreground.Read(update);
        _updates.Writer.TryWrite(update);
    }

    void IJsonRpcTarget.OnClosed(Exception reason)
    {
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
    }
}
