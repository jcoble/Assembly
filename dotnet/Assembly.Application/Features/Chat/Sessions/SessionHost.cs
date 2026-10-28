using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V1;
using Assembly.Application.Common.Calls;
using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;

using Assembly.Application.Common.Agents;
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
    private readonly Lock _gate = new();
    // Held from a turn's open to its close, so a quick next send resumes only once the session is closed.
    private readonly SemaphoreSlim _session = new(1, 1);
    private RunningTurn? _turn;
    // The last turn's work, which goes on past its end rows until its session is closed.
    private Task _lastTurn = Task.CompletedTask;
    // Sends with a clientId that haven't answered yet, by that id, with their text. Guarded by _gate.
    private readonly Dictionary<string, (string Text, Task<string?> Send)> _sending = [];
    // The asks waiting for the user, by id. Guarded by _gate.
    private readonly Dictionary<string, OpenAsk> _asks = [];

    // Background work still going, by task id. Guarded by _gate.
    private readonly Dictionary<string, TaskChanged> _tasks = [];
    // The session a turn left open for its background work, with what reads its updates. Guarded by _gate.
    private KeptSession? _kept;
    private bool _shuttingDown;
    // The subagents the agent runs as child sessions. Guarded by _gate.
    private readonly Subagents _subagents;
    // Whether a helper session this session's agent started is at work; and who to tell when a turn here ends.
    private readonly Func<bool> _helpersWorking;
    private readonly Action<string>? _turnEnded;
    // Who to tell when this session's ACP session closes.
    private readonly Action<string>? _sessionClosed;
    // Names the session once its first turn has ended.
    private readonly SessionTitler? _titler;

    /// <summary>A self-started run with no word from the agent this long, no tool running and no task open, ends.</summary>
    public const long QuietMs = 30_000;

    private sealed record OpenAsk(AskPayload Card, TaskCompletionSource<JsonElement> Answer);

    /// <summary>How long a turn that ended with a tool call still running keeps its session for a task to show up.</summary>
    public const long TaskGraceMs = 2_000;

    private sealed record KeptSession(OpenSession Session, ChannelReader<SessionNotification> Updates, Task Pump,
        CancellationTokenSource Ticking, Task Ticks, long TasksDueBy);

    public SessionHost(string sessionId, LiveHub hub, SessionStore sessionStore, SessionOpener opener, TimeProvider time, TextWriter log,
        BackgroundWork work, Func<bool>? helpersWorking = null, Action<string>? turnEnded = null, Action<string>? sessionClosed = null,
        SessionTitler? titler = null)
    {
        _sessionClosed = sessionClosed;
        _titler = titler;
        _helpersWorking = helpersWorking ?? (() => false);
        _turnEnded = turnEnded;
        _sessionId = sessionId;
        _hub = hub;
        _sessionStore = sessionStore;
        _opener = opener;
        _time = time;
        _log = log;
        _work = work;
        _subagents = new Subagents(sessionStore, hub);
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

    /// <summary>Starts a turn and returns its run id, or null if one is already running.</summary>
    public string? Send(string text, string? clientId = null)
    {
        RunningTurn turn;
        KeptSession? kept;
        lock (_gate)
        {
            if (_turn is not null)
                return null;
            // A session kept open for background work takes the prompt as it is.
            (kept, _kept) = (_kept, null);
            turn = _turn = new RunningTurn(Guid.NewGuid().ToString(), new StageTimes(_time)) { Session = kept?.Session };
            Apply(new TurnStarted(Now(), turn.RunId, text, clientId));
        }

        _sessionStore.Touch(_sessionId);
        _lastTurn = turn.Done = Task.Run(() => RunAsync(turn, text, kept));
        return turn.RunId;
    }

    /// <summary>
    /// Sends <paramref name="text"/> into the running turn, through the agent's <c>_session/steering</c>, and returns
    /// that turn's run id; with no turn running, starts one. Null when the turn can't take it: its session isn't open
    /// yet, or the agent doesn't steer. A send with a <paramref name="clientId"/> already sent in this session answers
    /// that send's run id, or waits for it while it's still going, and never starts a second turn or steer.
    /// </summary>
    public async Task<string?> SteerAsync(string text, string? clientId, CancellationToken cancellationToken)
    {
        if (clientId is null)
            return await SteerOnceAsync(text, null, cancellationToken);
        Task<string?> sending;
        TaskCompletionSource<string?>? mine = null;
        lock (_gate)
        {
            if (_sending.TryGetValue(clientId, out var going))
            {
                SameText(clientId, going.Text, text);
                sending = going.Send;
                _log.WriteLine($"send {clientId}: joined in-flight send");
            }
            else if (_hub.UserRowOfClient(_sessionId, clientId) is { } row)
            {
                SameText(clientId, row.Text, text);
                return row.RunId;
            }
            else
            {
                mine = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _sending[clientId] = (text, sending = mine.Task);
            }
        }

        if (mine is not null)
            return await SendOnceAsync(text, clientId, mine, cancellationToken);
        try
        {
            // Its own token: a retry that gives up doesn't cancel the send it waits on.
            return await sending.WaitAsync(cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // The send it joined failed, timed out or was cancelled, maybe after its request went out: it sends
            // nothing more. A row says the message landed; otherwise it answers that failure.
            lock (_gate)
                if (_hub.UserRowOfClient(_sessionId, clientId) is { } row)
                    return row.RunId;
            if (error is OperationCanceledException)
                throw new CallFailed("this message's first send was cut off; it may have reached the agent");
            throw;
        }
    }

    // The one send of a clientId in flight. It leaves the map before it answers, so a later send looks at the rows.
    private async Task<string?> SendOnceAsync(string text, string clientId, TaskCompletionSource<string?> mine, CancellationToken cancellationToken)
    {
        try
        {
            var runId = await SteerOnceAsync(text, clientId, cancellationToken);
            lock (_gate)
                _sending.Remove(clientId);
            mine.SetResult(runId);
            return runId;
        }
        catch (Exception error)
        {
            lock (_gate)
                _sending.Remove(clientId);
            mine.SetException(error);
            throw;
        }
    }

    // A clientId names one message: the same id with other text is refused.
    private void SameText(string clientId, string sent, string text)
    {
        if (sent == text)
            return;
        _log.WriteLine($"Warning: session {_sessionId}: clientId {clientId} was sent again with other text");
        throw new CallFailed("clientId already used for another message");
    }

    private async Task<string?> SteerOnceAsync(string text, string? clientId, CancellationToken cancellationToken)
    {
        RunningTurn? turn;
        lock (_gate)
            turn = _turn;
        if (turn is null)
            return Send(text, clientId);
        if (turn.Session is not { } session || !session.Adapter.Init.CanSteer)
            return null;

        SteerResponse steered;
        try
        {
            steered = await session.Adapter.Link.Client.SteerAsync(session.AcpSessionId, text, cancellationToken);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException)
        {
            return null;
        }

        switch (steered.Outcome)
        {
            case "injected":
                // Taken, so it's saved on its turn even if that turn has just ended.
                lock (_gate)
                    Apply(new TurnSteered(Now(), turn.RunId, text, clientId));
                _sessionStore.Touch(_sessionId);
                return turn.RunId;
            case "promptRequired":
                // The turn ended as the message went: it starts the next one, once this one's session is closed.
                await turn.Done.WaitAsync(cancellationToken);
                return Send(text, clientId);
            default:
                return null;
        }
    }

    public async Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        var options = request.Options.Select(option => new AskOption(option.OptionId, option.Name, option.Kind)).ToList();
        var card = new AskPayload("permission", "open", Titled(request.SessionId, request.ToolTitle), null, ToolCallId(request.ToolCall), options, null, null);
        var answer = await AskAsync(card, cancellationToken);
        return PermissionOutcome.Selected(answer.GetProperty("optionId").GetString()!);
    }

    public async Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken cancellationToken)
    {
        var card = new AskPayload("question", "open", Titled(request.SessionId, null), request.Message, request.ToolCallId, null, request.RequestedSchema.Clone(), null);
        var answer = await AskAsync(card, cancellationToken);
        return new ElicitationResponse(answer.GetProperty("action").GetString()!,
            answer.TryGetProperty("content", out var content) ? content.Clone() : default);
    }

    /// <summary>
    /// Answers the open ask <paramref name="askId"/>: <c>{ optionId }</c>, one of a permission's options, or
    /// <c>{ action: accept|decline|cancel, content? }</c> for a question. False if it isn't open or the answer doesn't fit.
    /// </summary>
    public bool Answer(string askId, JsonElement answer)
    {
        OpenAsk? ask;
        lock (_gate)
            _asks.TryGetValue(askId, out ask);
        var fits = ask?.Card.Kind switch
        {
            "permission" => answer.TryGetProperty("optionId", out var option) && option.ValueKind == JsonValueKind.String
                            && ask.Card.Options!.Any(o => o.OptionId == option.GetString()),
            "question" => answer.TryGetProperty("action", out var action) && action.GetString() is "accept" or "decline" or "cancel"
                          && (!answer.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Object),
            _ => false,
        };
        return fits && ask!.Answer.TrySetResult(answer.Clone());
    }

    // Shows the card and waits for its answer; a cancel settles the card as cancelled and goes on up.
    private async Task<JsonElement> AskAsync(AskPayload card, CancellationToken cancellationToken)
    {
        var askId = Guid.NewGuid().ToString();
        var ask = new OpenAsk(card, new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            _asks[askId] = ask;
            Apply(new AskChanged(Now(), askId, card));
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
                Apply(new AskChanged(Now(), askId, card with { State = answer is null ? "cancelled" : "answered", Answer = answer }));
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

        OpenSession? session;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task) || !task.CanStop)
                return false;
            session = _turn?.Session ?? _kept?.Session;
        }

        if (session is null)
            return false;
        try
        {
            return (await session.Adapter.Link.Client.StopTaskAsync(session.AcpSessionId, taskId, cancellationToken)).Stopped;
        }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException)
        {
            return false;
        }
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
            return _lastTurn;
        }
    }

    private async Task RunAsync(RunningTurn turn, string text, KeptSession? kept)
    {
        // The ticker and the update pump last as long as the session: a kept session hands its own on.
        var ticking = kept?.Ticking ?? new CancellationTokenSource();
        var ticks = kept?.Ticks ?? TickAsync(ticking.Token);
        var session = kept?.Session;
        var updates = kept?.Updates;
        var pump = kept?.Pump ?? Task.CompletedTask;
        var lost = false;
        var held = kept is not null;
        // The turn that gave the session its first title is its first.
        var first = false;
        Func<long, AgentEvent> end;
        try
        {
            if (session is null)
            {
                await _session.WaitAsync(turn.Stopping.Token);
                held = true;
                session = await _opener.OpenAsync(_sessionId, text, turn.Stages, turn.Stopping.Token);
                first = session.Titled;
                updates = session.Adapter.Updates.For(session.AcpSessionId);
                session.Adapter.Link.Client.SetSessionPolicy(session.AcpSessionId, this);
                lock (_gate)
                {
                    turn.Session = session;
                    turn.Opened.TrySetResult();
                    Apply(new AgentStarted(Now(), session.Resumed, session.SavedCost, session.Model));
                }

                pump = PumpAsync(updates);
            }
            // A stop that lands between here and the prompt reaching the agent is lost; the turn then runs to its end.
            if (turn.Stopping.IsCancellationRequested)
            {
                end = at => new TurnEnded(at, "cancelled", null);
            }
            else
            {
                var response = await session.Adapter.Link.Client.PromptAsync(session.AcpSessionId, text, CancellationToken.None);
                turn.Stages.Mark("answer");
                end = at => UpdateReader.TurnEnd(response, at);
            }
        }
        catch (OperationCanceledException) when (turn.Stopping.IsCancellationRequested)
        {
            end = at => new TurnEnded(at, "cancelled", null);
        }
        catch (JsonRpcException error)
        {
            end = at => UpdateReader.TurnFailure(error, at);
        }
        catch (IOException) when (session is not null)
        {
            // The pipe closed under the prompt: the adapter is gone, whether or not its exit has been seen yet.
            // Its process group is killed as it exits, and the next send starts a new adapter.
            lost = true;
            end = at => new AgentLost(at);
        }
        catch (Exception error)
        {
            end = at => new TurnFailed(at, error.Message, null);
        }

        decimal? sessionCost;
        var keep = false;
        var toolsOpen = false;
        var title = false;
        lock (_gate)
        {
            try
            {
                // Everything the agent sent before its answer has arrived by now, so it goes in before the end.
                if (session is not null && updates is not null)
                {
                    session.Adapter.Updates.Flush();
                    Drain(updates);
                }

                // A tool call still running as the turn ends may be work the agent moved to the background
                // and tells us of only after its answer (Codex's async_task_spawned can follow the prompt's result).
                toolsOpen = _converter.HasOpenTools;
                var ended = end(Now());
                Apply(ended);
                title = first && ended is TurnEnded { StopReason: not "cancelled" };
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"session {_sessionId}: the turn's end wasn't saved: {error.Message}");
            }
            finally
            {
                // Even if saving the end failed, the session takes sends again.
                sessionCost = _converter.SessionCost;
                _turn = null;
                turn.Opened.TrySetResult();
                // Background work still going keeps the session, and with it the work, alive.
                var working = _tasks.Count > 0 || _subagents.AnyRunning || _helpersWorking();
                keep = session is not null && updates is not null && !lost && !_shuttingDown && (working || toolsOpen);
                if (keep)
                    _kept = new KeptSession(session!, updates!, pump, ticking, ticks, TasksDueBy: working ? 0 : Now() + TaskGraceMs);
            }
        }

        _turnEnded?.Invoke(_sessionId);
        if (title && _titler is { } titler)
            _work.Start($"title {_sessionId}", stopping => titler.TitleAsync(_sessionId, text, turn.RunId, stopping));
        if (keep)
        {
            _log.WriteLine($"session {_sessionId} run {turn.RunId}: {turn.Stages}");
            _log.WriteLine($"session {_sessionId}: session kept open for background work");
            return;
        }

        if (session is not null)
            await CloseAsync(session, turn.Stages, sessionCost, lost, pump);
        if (held)
            _session.Release();
        _log.WriteLine($"session {_sessionId} run {turn.RunId}: {turn.Stages}");
        await ticking.CancelAsync();
        await ticks;
        ticking.Dispose();
    }

    // Called with the lock held.
    /// <summary>A helper session's row in this session, as it stands now.</summary>
    internal void Show(SubagentShown helper)
    {
        lock (_gate)
            Apply(helper);
    }

    private void Apply(AgentEvent agentEvent) => _hub.Publish(_sessionId, _converter.Apply(agentEvent));

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
