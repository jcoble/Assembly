using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp;

namespace Assembly.Application.Common.Acp;

/// <summary>
/// The client side of ACP v1 for one agent process: typed calls out, and the two things an agent
/// sends us: <c>session/update</c> notifications (queued on <see cref="Updates"/>) and permission requests.
/// </summary>
public sealed class AcpClient : IJsonRpcTarget, IAsyncDisposable
{
    public const int ProtocolVersion = 1;

    private readonly IPermissionPolicy _permissions;
    // Who answers a session's prompts while it's open, in place of the client-wide policy.
    private readonly ConcurrentDictionary<string, IPermissionPolicy> _sessionPolicies = new();
    private readonly Channel<SessionNotification> _updates =
        Channel.CreateUnbounded<SessionNotification>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTurns = new();
    private readonly JsonRpcConnection _rpc;

    public AcpClient(Stream fromAgent, Stream toAgent, IPermissionPolicy permissions, TranscriptLog transcript)
    {
        _permissions = permissions;
        _rpc = new JsonRpcConnection(fromAgent, toAgent, this, transcript);
    }

    /// <summary>
    /// Sends <paramref name="sessionId"/>'s permission prompts and questions to <paramref name="policy"/>, or back to
    /// the client-wide policy when it's null.
    /// </summary>
    public void SetSessionPolicy(string sessionId, IPermissionPolicy? policy)
    {
        if (policy is null)
            _sessionPolicies.TryRemove(sessionId, out _);
        else
            _sessionPolicies[sessionId] = policy;
    }

    /// <summary>Every <c>session/update</c>, in arrival order. Completes when the agent's output closes.</summary>
    public ChannelReader<SessionNotification> Updates => _updates.Reader;

    /// <summary>Starts the connection, opting into the AIR extensions named in <paramref name="air"/>, if any.</summary>
    public Task<InitializeResponse> InitializeAsync(Implementation clientInfo, IReadOnlyList<string>? air, CancellationToken cancellationToken) =>
        RequestAsync(
            AcpMethods.Initialize,
            new InitializeRequest(ProtocolVersion, ClientCapabilities.WithAir(air), clientInfo),
            AcpJsonContext.Default.InitializeRequest,
            AcpJsonContext.Default.InitializeResponse,
            cancellationToken);

    /// <summary>A new session in <paramref name="cwd"/>, joined to <paramref name="mcpServers"/> (ACP <c>McpServer</c>s), if any.</summary>
    public Task<NewSessionResponse> NewSessionAsync(string cwd, CancellationToken cancellationToken, IReadOnlyList<JsonElement>? mcpServers = null) =>
        RequestAsync(
            AcpMethods.SessionNew,
            new NewSessionRequest(cwd, mcpServers ?? []),
            AcpJsonContext.Default.NewSessionRequest,
            AcpJsonContext.Default.NewSessionResponse,
            cancellationToken);

    /// <summary>Reopens <paramref name="sessionId"/>. Only for an agent whose <see cref="InitializeResponse.CanResumeSessions"/> is true.</summary>
    /// <remarks>
    /// The answer queues <see cref="SessionNotification.Resumed"/> on <see cref="Updates"/> in pipe order, so a reader
    /// can tell the session's updates sent before the answer from those sent after it.
    /// </remarks>
    public Task<ResumeSessionResponse> ResumeSessionAsync(string sessionId, string cwd, CancellationToken cancellationToken,
        IReadOnlyList<JsonElement>? mcpServers = null) =>
        RequestAsync(
            AcpMethods.SessionResume,
            new ResumeSessionRequest(sessionId, cwd, mcpServers ?? []),
            AcpJsonContext.Default.ResumeSessionRequest,
            AcpJsonContext.Default.ResumeSessionResponse,
            cancellationToken,
            () => _updates.Writer.TryWrite(SessionNotification.Resumed(sessionId)));

    /// <summary>Ends <paramref name="sessionId"/> in the adapter. A later <see cref="ResumeSessionAsync"/> reopens it.</summary>
    public Task<CloseSessionResponse> CloseSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        RequestAsync(
            AcpMethods.SessionClose,
            new CloseSessionRequest(sessionId),
            AcpJsonContext.Default.CloseSessionRequest,
            AcpJsonContext.Default.CloseSessionResponse,
            cancellationToken);

    /// <summary>Titles <paramref name="sessionId"/>. Only for an agent whose <see cref="InitializeResponse.CanRenameSessions"/> is true.</summary>
    public Task<RenameSessionResponse> RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken) =>
        RequestAsync(
            AcpMethods.SessionRename,
            new RenameSessionRequest(sessionId, title),
            AcpJsonContext.Default.RenameSessionRequest,
            AcpJsonContext.Default.RenameSessionResponse,
            cancellationToken);

    public Task<SetConfigOptionResponse> SetConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken) =>
        RequestAsync(
            AcpMethods.SessionSetConfigOption,
            new SetConfigOptionRequest(sessionId, configId, value),
            AcpJsonContext.Default.SetConfigOptionRequest,
            AcpJsonContext.Default.SetConfigOptionResponse,
            cancellationToken);

    /// <summary>Runs one turn. Returns when the agent answers the prompt, which is after its last update.</summary>
    public async Task<PromptResponse> PromptAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        using var turn = new CancellationTokenSource();
        if (!_runningTurns.TryAdd(sessionId, turn))
            throw new InvalidOperationException($"Session {sessionId} already has a turn running.");
        try
        {
            return await RequestAsync(
                AcpMethods.SessionPrompt,
                new PromptRequest(sessionId, [TextBlock.Of(text)]),
                AcpJsonContext.Default.PromptRequest,
                AcpJsonContext.Default.PromptResponse,
                cancellationToken);
        }
        finally
        {
            // Whatever the turn asked and nobody answered is cancelled with it, as when the agent goes.
            _runningTurns.TryRemove(sessionId, out _);
            await turn.CancelAsync();
        }
    }

    /// <summary>Gives the running turn another message. An idle session answers <c>promptRequired</c> and runs nothing.</summary>
    public Task<SteerResponse> SteerAsync(string sessionId, string text, CancellationToken cancellationToken) =>
        RequestAsync(
            AcpMethods.SessionSteering,
            new SteerRequest(sessionId, [TextBlock.Of(text)], new SteerMeta(new SteerOptions("promptRequired"))),
            AcpJsonContext.Default.SteerRequest,
            AcpJsonContext.Default.SteerResponse,
            cancellationToken);

    /// <summary>Stops a background task the agent said it can stop.</summary>
    public Task<StopTaskResponse> StopTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken) =>
        RequestAsync(
            AcpMethods.SessionStopTask,
            new StopTaskRequest(sessionId, taskId),
            AcpJsonContext.Default.StopTaskRequest,
            AcpJsonContext.Default.StopTaskResponse,
            cancellationToken);

    /// <summary>
    /// Asks the agent to stop the running turn. The prompt call still returns normally, with
    /// <c>cancelled</c>. ACP requires us to settle any open permission prompt as cancelled first.
    /// </summary>
    public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_runningTurns.TryGetValue(sessionId, out var turn))
        {
            try
            {
                turn.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The turn ended while we were cancelling it.
            }
        }

        var notification = JsonSerializer.SerializeToElement(
            new CancelNotification(sessionId), AcpJsonContext.Default.CancelNotification);
        return _rpc.NotifyAsync(AcpMethods.SessionCancel, notification, cancellationToken);
    }

    async Task<JsonElement> IJsonRpcTarget.OnRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        // We offered no fs/* or terminal/* capability, so permissions and form questions are all we expect.
        switch (method)
        {
            case AcpMethods.SessionRequestPermission:
                var request = parameters.Deserialize(AcpJsonContext.Default.RequestPermissionRequest)
                    ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing permission request.");
                var outcome = await AskAsync(request.SessionId, PermissionOutcome.Cancelled,
                    (policy, token) => policy.DecideAsync(request, token), cancellationToken);
                return JsonSerializer.SerializeToElement(new RequestPermissionResponse(outcome), AcpJsonContext.Default.RequestPermissionResponse);
            case AcpMethods.ElicitationCreate:
                var question = parameters.Deserialize(AcpJsonContext.Default.ElicitationRequest)
                    ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing elicitation request.");
                var answer = question.Mode != "form"
                    ? ElicitationResponse.Cancel
                    : await AskAsync(question.SessionId, ElicitationResponse.Cancel, (policy, token) => policy.ElicitAsync(question, token), cancellationToken);
                return JsonSerializer.SerializeToElement(answer, AcpJsonContext.Default.ElicitationResponse);
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {method}");
        }
    }

    // Only a running turn's session is asked; a cancel of the turn, or the agent going, settles the ask as cancelled.
    private async Task<T> AskAsync<T>(string sessionId, T cancelled, Func<IPermissionPolicy, CancellationToken, Task<T>> ask, CancellationToken connectionClosing)
    {
        if (!_runningTurns.TryGetValue(sessionId, out var turn))
            return cancelled;

        try
        {
            using var either = CancellationTokenSource.CreateLinkedTokenSource(connectionClosing, turn.Token);
            return await ask(_sessionPolicies.GetValueOrDefault(sessionId) ?? _permissions, either.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return cancelled;
        }
    }

    void IJsonRpcTarget.OnNotification(string method, JsonElement parameters)
    {
        if (method != AcpMethods.SessionUpdate)
            return;
        if (parameters.Deserialize(AcpJsonContext.Default.SessionNotification) is { } update)
            _updates.Writer.TryWrite(update);
    }

    void IJsonRpcTarget.OnClosed(Exception reason) => _updates.Writer.TryComplete();

    private async Task<TResponse> RequestAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken,
        Action? onReply = null)
    {
        var result = await _rpc.RequestAsync(method, JsonSerializer.SerializeToElement(request, requestType), cancellationToken, onReply);
        return result.Deserialize(responseType)
            ?? throw new JsonRpcException(JsonRpcException.InternalError, $"{method} returned no result.");
    }

    public ValueTask DisposeAsync() => _rpc.DisposeAsync();
}
