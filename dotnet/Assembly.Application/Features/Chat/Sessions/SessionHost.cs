using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Application.Common.Calls;
using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;

using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Engine;
namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// One session: its converter and the turn running now. Each turn opens the session's ACP session on the shared adapter
/// and closes it when the turn ends, so an idle session holds no agent process. Every event takes one lock on its
/// way through (read, convert, commit, push), so the converter sees events in the order they happened, and a
/// turn's end comes after every update the agent sent before it answered the prompt. What the agent asks the user
/// while its session is open (a permission, a question) becomes a card row, and waits for <see cref="Answer"/>.
/// A turn that ends with background work still going keeps its session open until that work is done; what the agent
/// does meanwhile with no prompt running is a run it started itself, which ends when the agent says its cycle is over.
/// </summary>
public sealed partial class SessionHost : IPermissionPolicy
{
    private readonly string _sessionId;
    private readonly LiveHub _hub;
    private readonly SessionStore _sessionStore;
    private readonly SessionOpener _opener;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;
    private readonly BackgroundWork _work;
    private readonly RowConverter _converter = new();
    private readonly ISessionFeed? _feed;
    private readonly Lock _gate = new();
    // Held from a turn's open to its close, so a quick next send resumes only once the session is closed.
    private readonly SemaphoreSlim _session = new(1, 1);
    private RunningTurn? _turn;
    // The last turn's work, which goes on past its end rows until its session is closed.
    private Task _lastTurn = Task.CompletedTask;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Text, string? Label, string? FromSessionId,
        Task<SendResult> Send, string? EffectId, string RunId, IReadOnlyList<string> AttachmentIds)> _sending = new();
    private (OwningSendAdmission Delivery, UserRowReference? User, Task Done)? _lastDelivery;
    private readonly Dictionary<string, (string Text, string? Label, string? FromSessionId, Task<SteerDeliveryResult> Send)> _steering = [];
    // The asks waiting for the user, by id. Guarded by _gate.
    private readonly Dictionary<string, OpenAsk> _asks = [];

    // Background work still going, by task id. Guarded by _gate.
    private readonly Dictionary<string, TaskChanged> _tasks = [];
    // The session a turn left open for its background work, with what reads its updates. Guarded by _gate.
    private KeptSession? _kept;
    private bool _shuttingDown;
    // The subagents the agent runs as child sessions. Guarded by _gate.
    private readonly Subagents _subagents;
    // Whether a session this session's agent started is at work; and who to tell when a turn here ends.
    private readonly Func<bool> _startedWorking;
    private readonly Action<string>? _turnEnded;
    // Who to tell when this session's ACP session closes.
    private readonly Action<string>? _sessionClosed;
    // Names the session once a turn has ended normally after the open that gave it its first title.
    private readonly SessionTitler? _titler;
    // Set when an open gave the session its first title, until a turn that ends normally starts the titler. Kept in
    // memory only, so a session titled before a restart is never retitled. Guarded by _gate.
    private bool _titlePending;

    /// <summary>A self-started run with no word from the agent this long, no tool running and no task open, ends.</summary>
    public const long QuietMs = 30_000;

    private sealed record OpenAsk(AskPayload Card, TaskCompletionSource<JsonElement> Answer,
        SessionObservation? Observation, JsonRpcRequestContext? Request, string AcpSessionId);

    /// <summary>How long a turn that ended with a tool call still running keeps its session for a task to show up.</summary>
    public const long TaskGraceMs = 2_000;

    private sealed record KeptSession(OpenSession Session, ChannelReader<SessionNotification> Updates, Task Pump,
        CancellationTokenSource Ticking, Task Ticks, long TasksDueBy, SessionObservation? Observation, RunningTurn ReleaseTurn);

    public SessionHost(string sessionId, LiveHub hub, SessionStore sessionStore, SessionOpener opener, TimeProvider time, TextWriter log,
        BackgroundWork work, Func<bool>? startedWorking = null, Action<string>? turnEnded = null, Action<string>? sessionClosed = null,
        SessionTitler? titler = null, ISessionFeed? feed = null)
    {
        _feed = feed;
        _sessionClosed = sessionClosed;
        _titler = titler;
        _startedWorking = startedWorking ?? (() => false);
        _turnEnded = turnEnded;
        _sessionId = sessionId;
        _hub = hub;
        _sessionStore = sessionStore;
        _opener = opener;
        _time = time;
        _log = log;
        _work = work;
        _subagents = new Subagents();
    }

    public string? ActiveRunId
    {
        get
        {
            lock (_gate)
                return _turn?.RunId;
        }
    }

    /// <summary>Whether the session's ACP session is open: a turn running, or the session kept for background work.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _turn is not null || _kept is not null;
        }
    }

    public Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken) =>
        DecideAsync(request, cancellationToken, null, null);

    private async Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken,
        SessionObservation? observed, JsonRpcRequestContext? context, Func<bool>? providerGone = null)
    {
        var options = request.Options.Select(option => new AskOption(option.OptionId, option.Name, option.Kind)).ToList();
        var card = new AskPayload("permission", "open", Titled(request.SessionId, request.Title), request.Description, ToolCallId(request.ToolCall), options, null, null);
        var answer = await AskAsync(card, cancellationToken, observed, context, providerGone, request.SessionId, request);
        return PermissionOutcome.Selected(answer.GetProperty("optionId").GetString()!);
    }

    public Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken cancellationToken) =>
        ElicitAsync(request, cancellationToken, null, null);

    private async Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken cancellationToken,
        SessionObservation? observed, JsonRpcRequestContext? context, Func<bool>? providerGone = null)
    {
        var card = new AskPayload("question", "open", Titled(request.SessionId ?? "", null), request.Message, request.ToolCallId, null, request.RequestedSchema.Clone(), null);
        var answer = await AskAsync(card, cancellationToken, observed, context, providerGone, request.SessionId ?? "");
        return new ElicitationResponse(answer.GetProperty("action").GetString()!,
            answer.TryGetProperty("content", out var content) ? content.Clone() : default);
    }

    /// <summary>
    /// Answers the open ask <paramref name="askId"/>: <c>{ optionId }</c>, one of a permission's options, or
    /// <c>{ action: accept|decline|cancel, content? }</c> for a question. False if it isn't open or the answer doesn't fit.
    /// </summary>
    public bool Answer(string askId, JsonElement answer)
    {
        lock (_gate)
            return _asks.TryGetValue(askId, out var ask) && FitsAnswer(ask.Card, answer)
                && ask.Answer.TrySetResult(answer.Clone());
    }

    /// <summary>Captures the original live provider request without accepting or changing its answer.</summary>
    public ProviderAnswerBinding? CaptureProviderAnswerBinding(string askId)
    {
        lock (_gate) return CaptureProviderAnswerBindingLocked(askId, out _);
    }

    private ProviderAnswerBinding? CaptureProviderAnswerBindingLocked(string askId, out OpenAsk? ask)
    {
        if (!_asks.TryGetValue(askId, out ask) || ask.Answer.Task.IsCompleted ||
            ask.Observation is not { } observed || ask.Request is not { } request) return null;
        return new(_sessionId, askId, request.ProviderRequestId, observed, ask.AcpSessionId);
    }

    /// <summary>Captures one actual open request without completing it or entering a writer.</summary>
    public PreparedProviderAnswer? PrepareProviderAnswer(string askId, JsonElement answer)
    {
        lock (_gate)
        {
            var binding = CaptureProviderAnswerBindingLocked(askId, out var ask);
            if (binding is null) return null;
            if (ask!.Card.Kind == "permission" && answer.ValueKind == JsonValueKind.String)
                answer = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["optionId"] = answer.Clone() },
                    SessionsJson.Default.DictionaryStringJsonElement);
            if (!FitsAnswer(ask.Card, answer)) return null;
            return new(binding, answer.Clone());
        }
    }

    internal bool AnswerProviderRequest(ProviderAnswerBinding binding, JsonElement answer)
    {
        lock (_gate)
            return binding.SessionId == _sessionId && _asks.TryGetValue(binding.ChatRowId, out var ask) &&
                ask.Request?.ProviderRequestId == binding.ProviderRequestId && ask.Observation == binding.Observation &&
                binding.Observation.SessionId == _sessionId && ask.AcpSessionId == binding.AcpSessionId &&
                FitsAnswer(ask.Card, answer) && ask.Answer.TrySetResult(answer.Clone());
    }

    internal bool CancelProviderRequest(string providerRequestId, ProviderTerminalBinding binding)
    {
        lock (_gate)
            return binding.SessionId == _sessionId && _asks.TryGetValue(binding.ChatRowId, out var ask) &&
                ask.Request?.ProviderRequestId == providerRequestId && ask.Observation is { } observed &&
                observed.SessionId == _sessionId && observed.Epoch == binding.Epoch && observed.RunId == binding.RunId &&
                observed.StepId == binding.StepId && observed.Attempt == binding.Attempt && ask.Answer.TrySetCanceled();
    }

    private static bool FitsAnswer(AskPayload card, JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object) return false;
        return card.Kind switch
        {
            "permission" => answer.TryGetProperty("optionId", out var option) && option.ValueKind == JsonValueKind.String
                            && card.Options!.Any(o => o.OptionId == option.GetString()),
            "question" => answer.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String
                          && action.GetString() is "accept" or "decline" or "cancel"
                          && (!answer.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Object)
                          && (action.GetString() != "accept" || FitsForm(card.Schema, answer)),
            _ => false,
        };
    }

    private static bool FitsForm(JsonElement? requested, JsonElement answer)
    {
        if (requested is null || requested.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        var schema = requested.Value;
        if (schema.ValueKind != JsonValueKind.Object) return false;
        if (schema.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "object")) return false;
        var hasContent = answer.TryGetProperty("content", out var content);
        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array || required.EnumerateArray().Any(name =>
                name.ValueKind != JsonValueKind.String || !hasContent || !content.TryGetProperty(name.GetString()!, out _))) return false;
        }
        if (!hasContent) return true;
        var hasProperties = schema.TryGetProperty("properties", out var properties);
        if (hasProperties && properties.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in content.EnumerateObject())
        {
            if (!hasProperties || !properties.TryGetProperty(property.Name, out var field))
            {
                if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False) return false;
                continue;
            }
            if (field.ValueKind != JsonValueKind.Object) return false;
            if (field.TryGetProperty("type", out var fieldType) && fieldType.ValueKind != JsonValueKind.String) return false;
            if (fieldType.ValueKind == JsonValueKind.String && fieldType.GetString() == "array")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || !field.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Object || !property.Value.EnumerateArray().All(value => FitsChoice(items, value))) return false;
            }
            else if (!FitsChoice(field, property.Value)) return false;
        }
        return true;
    }

    private static bool FitsChoice(JsonElement field, JsonElement value)
    {
        if (field.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String)
        {
            if (kind.GetString() == "boolean") return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            if (kind.GetString() == "integer") return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
            if (kind.GetString() == "number") return value.ValueKind == JsonValueKind.Number;
        }
        if (value.ValueKind != JsonValueKind.String) return false;
        if (field.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "string")) return false;
        if (field.TryGetProperty("enum", out var options))
            return options.ValueKind == JsonValueKind.Array && options.EnumerateArray().Any(option => JsonElement.DeepEquals(option, value));
        if (field.TryGetProperty("oneOf", out var choices) || field.TryGetProperty("anyOf", out choices))
            return choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(option => option.ValueKind == JsonValueKind.Object &&
                option.TryGetProperty("const", out var id) && JsonElement.DeepEquals(id, value));
        return true;
    }

    // Shows the card and waits for its answer; a cancel settles the card as cancelled and goes on up.
    private async Task<JsonElement> AskAsync(AskPayload card, CancellationToken cancellationToken,
        SessionObservation? observed, JsonRpcRequestContext? request, Func<bool>? providerGone, string acpSessionId,
        RequestPermissionRequest? permission = null)
    {
        var askId = Guid.NewGuid().ToString();
        var ask = new OpenAsk(card, new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously), observed, request, acpSessionId);
        lock (_gate)
        {
            _asks[askId] = ask;
            if (_feed is not null && request is null) _log.WriteLine("engine feed: provider_request_context_missing");
            try
            {
                var prepared = observed is null || request is null ? null : _feed?.PrepareAsked(observed, askId, card, request);
                var batch = new RowBatch();
                if (permission is not null)
                    foreach (var change in _updateReader.ReadPermission(permission, Now()))
                        Apply(change, batch);
                Apply(new AskChanged(Now(), askId, card), batch);
                _hub.Publish(_sessionId, batch, prepared);
            }
            catch
            {
                _asks.Remove(askId);
                throw;
            }
        }

        JsonElement? answer = null;
        try
        {
            using (cancellationToken.Register(() => ask.Answer.TrySetCanceled(cancellationToken)))
                answer = await ask.Answer.Task;
            return answer.Value;
        }
        finally
        {
            lock (_gate)
            {
                _asks.Remove(askId);
                var prepared = observed is null || request is null ? null : _feed?.PrepareSettled(observed, askId,
                    answer is null ? "cancelled" : "answered", answer,
                    answer is not null ? "manual_answered" : providerGone?.Invoke() == true ? "provider_gone" : "turn_cancelled");
                Apply(new AskChanged(Now(), askId, card with { State = answer is null ? "cancelled" : "answered", Answer = answer }),
                    observe: prepared);
            }
        }
    }

    // A subagent's ask is shown here, in its parent's session, under the subagent's title.
    private string? Titled(string acpSessionId, string? title)
    {
        lock (_gate)
            return _subagents.Title(acpSessionId) is not { } subagent ? title : title is null ? subagent : $"{subagent}: {title}";
    }

    private static string? ToolCallId(JsonElement toolCall) =>
        toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("toolCallId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    /// <summary>Stops the turn if <paramref name="runId"/> is the one running. Its end arrives as rows.</summary>
    public bool Stop(string runId)
    {
        RunningTurn turn;
        OpenSession? session;
        lock (_gate)
        {
            if (_turn is null || _turn.RunId != runId)
                return false;
            (turn, session) = (_turn, _turn.Session);
            // No prompt waits on a run the agent started, so it ends here; the cancel ends the agent's cycle.
            if (turn.SelfStarted)
            {
                EndSelfRun(new TurnEnded(Now(), "cancelled", null));
                CloseIfDone();
            }
        }

        turn.Stopping.Cancel();
        if (session is not null)
            _work.Start("cancel " + runId, stopping => CancelQuietlyAsync(session, stopping));
        return true;
    }

    /// <summary>Stops the background task <paramref name="taskId"/>. False if it isn't running, can't be stopped, or the agent said no.</summary>
    public async Task<bool> StopTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        // A subagent stops through the same route, by its child session id.
        bool subagent;
        lock (_gate)
            subagent = !_tasks.ContainsKey(taskId) && _subagents.Has(taskId);
        if (subagent)
            return await StopSubagentAsync(taskId, cancellationToken);

        return false; // Generic tool work has no standard ACP v2 current-work cancellation.
    }

    /// <summary>Stops a subagent's current work (<c>session/cancel</c> with its child session id); its parent goes on.</summary>
    public async Task<bool> StopSubagentAsync(string childSessionId, CancellationToken cancellationToken)
    {
        OpenSession? session;
        lock (_gate)
        {
            if (!_subagents.Stoppable.Contains(childSessionId))
                return false;
            session = _turn?.Session ?? _kept?.Session;
        }

        if (session is null)
            return false;
        try
        {
            await session.Adapter.Link.Client.CancelAsync(childSessionId, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// For shutdown, before the adapters go: closes a session kept for background work, stopping that work first.
    /// From here on, no turn keeps its session.
    /// </summary>
    public async Task CloseKeptForShutdownAsync()
    {
        KeptSession? kept;
        lock (_gate)
        {
            _shuttingDown = true;
            if (_turn is { SelfStarted: true })
                EndSelfRun(new TurnEnded(Now(), "cancelled", null));
            // A user's turn still running decides for itself, and won't keep its session now.
            kept = _turn is null ? _kept : null;
            if (kept is not null)
                _kept = null;
        }

        if (kept is not null)
            await CloseKeptAsync(kept, lost: false);
    }

    /// <summary>Stops the running turn, if any, for shutdown, and returns when the last turn's work is done.</summary>
    public Task StopForShutdownAsync()
    {
        lock (_gate)
        {
            _shuttingDown = true;
            _turn?.Stopping.Cancel();
            if (_kept is { } kept && (_turn is null || _turn.SelfStarted))
            {
                if (_turn is { SelfStarted: true })
                    EndSelfRun(new TurnEnded(Now(), "cancelled", null));
                _kept = null;
                _lastTurn = Task.Run(() => CloseKeptAsync(kept, lost: false));
            }
            return _lastTurn;
        }
    }

    // Called with the lock held.
    /// <summary>The row in this session of a session its agent started, as it stands now.</summary>
    internal void Show(SubagentShown started)
    {
        lock (_gate)
            Apply(started);
    }

    private void Apply(AgentEvent agentEvent, RowBatch? collector = null, Action<Assembly.Application.Common.Database.IWriteScope>? observe = null, AuthTerminalProof? authProof = null)
    {
        var batch = _converter.Apply(agentEvent);
        if (authProof is not null) DecorateAuthTerminal(batch, authProof);
        if (collector is null) _hub.Publish(_sessionId, batch, observe);
        else
        {
            collector.Rows.AddRange(batch.Rows);
            collector.Details.AddRange(batch.Details);
            collector.Terminals.AddRange(batch.Terminals);
        }
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private static async Task CancelQuietlyAsync(OpenSession session, CancellationToken stopping)
    {
        try
        {
            await session.Adapter.Link.Client.CancelAsync(session.AcpSessionId, stopping);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The agent is gone, so the turn is ending anyway.
        }
    }

    private RunningTurn? _handoffExecution;
    private RunningTurn? _handoffCompleted;

    /// <summary>In-process physical ownership only. Public ACP identity is not a provider-native file witness.</summary>
    internal sealed class HandoffReleaseCapture(SessionHost owner, RunningTurn turn, OpenSession session, long generation)
    {
        internal SessionHost Owner { get; } = owner;
        internal RunningTurn Turn { get; } = turn;
        internal OpenSession Session { get; } = session;
        internal long Generation { get; } = generation;
        public SessionObservation Observation { get; } = turn.Observation!;
        public string SessionId => Observation.SessionId;
        public long Epoch => Observation.Epoch;
        public string AcpSessionId => Session.AcpSessionId;
        public string OperationId => Turn.RunId;
    }

    // Called under the host gate at the actual successful open, before the turn's open is published.
    private void RememberHandoffExecution(RunningTurn turn)
    {
        _handoffExecution = turn;
        _handoffCompleted = null;
    }

    // A lost execution or a failed cleanup must never become positive disposal evidence.
    private void MarkHandoffExecutionCompleted(RunningTurn turn, bool clean)
    {
        lock (_gate)
            if (clean && ReferenceEquals(_handoffExecution, turn)) _handoffCompleted = turn;
    }

    internal HandoffReleaseCapture? CaptureHandoffRelease(long expectedEpoch, string expectedAcpSessionId)
    {
        lock (_gate)
        {
            if (!HandoffIdle() || _handoffExecution is not { Session: { } session, Observation: { } observed } turn ||
                !ReferenceEquals(_handoffCompleted, turn) || observed.SessionId != _sessionId || observed.Epoch != expectedEpoch ||
                session.AcpSessionId != expectedAcpSessionId) return null;
            return new(this, turn, session, _warmGeneration);
        }
    }

    internal async Task ReleaseForHandoffAsync(HandoffReleaseCapture capture, CancellationToken ct)
    {
        lock (_gate) RequireHandoffCapture(capture);
        await capture.Turn.Done.WaitAsync(ct);
        lock (_gate) RequireHandoffCapture(capture);
        // Retire accepts the exact instance; a newer adapter is neither selected nor stopped by this capture.
        var disposed = await capture.Session.Pool.RetireAsync(capture.Session.Adapter, capture.AcpSessionId);
        if (!disposed) throw new CallFailed("Exact adapter disposal is unconfirmed.", "handoff_disposal_unconfirmed");
        // Retirement must never dispose a shared adapter that another execution still owns.
        await capture.Session.Adapter.Link.Exited.WaitAsync(ct);
        await capture.Session.Adapter.Updates.Completion.WaitAsync(ct);
        lock (_gate) RequireHandoffCapture(capture);
    }

    private bool HandoffIdle() => !_shuttingDown && _turn is null && _kept is null && _warm is null &&
        _asks.Count == 0 && _tasks.Count == 0 && !_subagents.AnyRunning && !_startedWorking();

    private void RequireHandoffCapture(HandoffReleaseCapture capture)
    {
        if (!ReferenceEquals(capture.Owner, this) || !ReferenceEquals(_handoffExecution, capture.Turn) ||
            !ReferenceEquals(_handoffCompleted, capture.Turn) || !ReferenceEquals(capture.Turn.Session, capture.Session) ||
            capture.Generation != _warmGeneration || !HandoffIdle())
            throw new CallFailed("The captured handoff execution changed.", "handoff_execution_changed");
    }

    internal async Task<string> ResumeForHandoffAsync(string expectedAcpSessionId, Action validate, CancellationToken ct)
    {
        lock (_gate)
            if (!HandoffIdle()) throw new CallFailed("The session has another execution.", "handoff_execution_changed");
        await _session.WaitAsync(ct);
        try
        {
            long generation;
            lock (_gate)
            {
                if (!HandoffIdle()) throw new CallFailed("The session has another execution.", "handoff_execution_changed");
                generation = Interlocked.Increment(ref _warmGeneration);
            }
            validate();
            var publicId = await _opener.ResumeForHandoffAsync(_sessionId, expectedAcpSessionId, new StageTimes(_time), ct, validate);
            lock (_gate)
                if (!HandoffIdle() || generation != _warmGeneration)
                    throw new CallFailed("The session execution changed during resume.", "handoff_execution_changed");
            return publicId;
        }
        finally { _session.Release(); }
    }
    internal SessionMaintenanceAttempt TryAcquireMaintenance()
    {
        if (!_session.Wait(0)) return new(null, "busy");
        lock (_gate)
        {
            if (_shuttingDown || _turn is not null || _kept is not null || _warm is not null || _asks.Count != 0 ||
                _tasks.Values.Any(task => task.Running) || _subagents.AnyRunning || _startedWorking())
            {
                _session.Release();
                return new(null, "busy");
            }
            return new(new MaintenanceLease(this, _session), null);
        }
    }

    internal Task<NativeResumeEvidence> ResumeNativeForMaintenanceAsync(string publicId, string folder, Action validate, CancellationToken ct) =>
        _opener.Native.ResumeAsync(_sessionId, publicId, folder, validate, ct);

    private sealed class MaintenanceLease(SessionHost owner, SemaphoreSlim session) : ISessionMaintenanceLease
    {
        private int _disposed;
        public NativeResumeEvidence? ResumeEvidence { get; private set; }
        public async Task<bool> ProbeResumeAsync(string providerSessionId, string folder, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            try
            {
                ResumeEvidence = await owner._opener.Native.ResumeAsync(owner._sessionId, providerSessionId, folder,
                    () => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this), ct);
                return true;
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                owner._log.WriteLine($"Native resume refused: {error.GetType().Name}");
                return false;
            }
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) session.Release();
            return ValueTask.CompletedTask;
        }
    }
}
