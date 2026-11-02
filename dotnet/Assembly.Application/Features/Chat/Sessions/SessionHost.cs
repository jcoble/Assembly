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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Text, string? Label, string? FromSessionId, Task<SendResult> Send)> _sending = new();
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
        CancellationTokenSource Ticking, Task Ticks, long TasksDueBy, SessionObservation? Observation);

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
        var answer = await AskAsync(card, cancellationToken, observed, context, providerGone, request.SessionId);
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

    /// <summary>Captures one actual open request without completing it or entering a writer.</summary>
    public PreparedProviderAnswer? PrepareProviderAnswer(string askId, JsonElement answer)
    {
        lock (_gate)
        {
            if (!_asks.TryGetValue(askId, out var ask) || ask.Answer.Task.IsCompleted ||
                ask.Observation is not { } observed || ask.Request is not { } request) return null;
            if (ask.Card.Kind == "permission" && answer.ValueKind == JsonValueKind.String)
                answer = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { ["optionId"] = answer.Clone() },
                    SessionsJson.Default.DictionaryStringJsonElement);
            if (!FitsAnswer(ask.Card, answer)) return null;
            return new(new(_sessionId, askId, request.ProviderRequestId, observed, ask.AcpSessionId), answer.Clone());
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
                          && (!answer.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Object),
            _ => false,
        };
    }

    // Shows the card and waits for its answer; a cancel settles the card as cancelled and goes on up.
    private async Task<JsonElement> AskAsync(AskPayload card, CancellationToken cancellationToken,
        SessionObservation? observed, JsonRpcRequestContext? request, Func<bool>? providerGone, string acpSessionId)
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
                Apply(new AskChanged(Now(), askId, card), observe: prepared);
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
}
