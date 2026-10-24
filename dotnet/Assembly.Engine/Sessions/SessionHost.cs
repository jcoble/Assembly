using System.Text.Json;
using System.Threading.Channels;
using Assembly.Engine.Acp;
using Assembly.Engine.Acp.JsonRpc;
using Assembly.Engine.Acp.Messages;
using Assembly.Engine.Acp.V1;
using Assembly.Engine.Events;
using Assembly.Engine.Live;
using Assembly.Engine.Rows;
using Assembly.Engine.Rows.Payloads;
using Assembly.Engine.Threads;

namespace Assembly.Engine.Sessions;

/// <summary>
/// One thread: its converter and the turn running now. Each turn opens the thread's session on the shared adapter
/// and closes it when the turn ends, so an idle thread holds no agent process. Every event takes one lock on its
/// way through (read, convert, commit, push), so the converter sees events in the order they happened, and a
/// turn's end comes after every update the agent sent before it answered the prompt. What the agent asks the user
/// while its session is open (a permission, a question) becomes a card row, and waits for <see cref="Answer"/>.
/// A turn that ends with background work still going keeps its session open until that work is done; what the agent
/// does meanwhile with no prompt running is a run it started itself, which ends when the agent says its cycle is over.
/// </summary>
public sealed class SessionHost : IPermissionPolicy
{
    private readonly string _threadId;
    private readonly LiveHub _hub;
    private readonly ThreadStore _threads;
    private readonly SessionOpener _opener;
    private readonly TimeProvider _time;
    private readonly TextWriter _log;
    private readonly RowConverter _converter = new();
    private readonly Lock _gate = new();
    // Held from a turn's open to its close, so a quick next send resumes only once the session is closed.
    private readonly SemaphoreSlim _session = new(1, 1);
    private RunningTurn? _turn;
    // The last turn's work, which goes on past its end rows until its session is closed.
    private Task _lastTurn = Task.CompletedTask;
    // The asks waiting for the user, by id. Guarded by _gate.
    private readonly Dictionary<string, OpenAsk> _asks = [];

    // Background work still going, by task id. Guarded by _gate.
    private readonly Dictionary<string, TaskChanged> _tasks = [];
    // The session a turn left open for its background work, with what reads its updates. Guarded by _gate.
    private KeptSession? _kept;
    private bool _shuttingDown;
    // The subagents the agent runs as child sessions. Guarded by _gate.
    private readonly Subagents _subagents;
    // Whether a helper session this thread's agent started is at work; and who to tell when a turn here ends.
    private readonly Func<bool> _helpersWorking;
    private readonly Action<string>? _turnEnded;
    // Who to tell when this thread's session closes.
    private readonly Action<string>? _sessionClosed;

    /// <summary>A self-started run with no word from the agent this long, no tool running and no task open, ends.</summary>
    public const long QuietMs = 30_000;

    private sealed record OpenAsk(AskPayload Card, TaskCompletionSource<JsonElement> Answer);

    /// <summary>How long a turn that ended with a tool call still running keeps its session for a task to show up.</summary>
    public const long TaskGraceMs = 2_000;

    private sealed record KeptSession(OpenSession Session, ChannelReader<SessionNotification> Updates, Task Pump,
        CancellationTokenSource Ticking, Task Ticks, long TasksDueBy);

    public SessionHost(string threadId, LiveHub hub, ThreadStore threads, SessionOpener opener, TimeProvider time, TextWriter log,
        Func<bool>? helpersWorking = null, Action<string>? turnEnded = null, Action<string>? sessionClosed = null)
    {
        _sessionClosed = sessionClosed;
        _helpersWorking = helpersWorking ?? (() => false);
        _turnEnded = turnEnded;
        _threadId = threadId;
        _hub = hub;
        _threads = threads;
        _opener = opener;
        _time = time;
        _log = log;
        _subagents = new Subagents(threads, hub);
    }

    public string? ActiveRunId
    {
        get
        {
            lock (_gate)
                return _turn?.RunId;
        }
    }

    /// <summary>Whether the thread's session is open: a turn running, or the session kept for background work.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _turn is not null || _kept is not null;
        }
    }

    /// <summary>Starts a turn and returns its run id, or null if one is already running.</summary>
    public string? Send(string text)
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
            Apply(new TurnStarted(Now(), turn.RunId, text));
        }

        _threads.Touch(_threadId);
        _lastTurn = turn.Done = Task.Run(() => RunAsync(turn, text, kept));
        return turn.RunId;
    }

    /// <summary>
    /// Sends <paramref name="text"/> into the running turn, through the agent's <c>_session/steering</c>, and returns
    /// that turn's run id; with no turn running, starts one. Null when the turn can't take it: its session isn't open
    /// yet, or the agent doesn't steer.
    /// </summary>
    public async Task<string?> SteerAsync(string text, CancellationToken cancellationToken)
    {
        RunningTurn? turn;
        lock (_gate)
            turn = _turn;
        if (turn is null)
            return Send(text);
        if (turn.Session is not { } session || !session.Adapter.Init.CanSteer)
            return null;

        SteerResponse steered;
        try
        {
            steered = await session.Adapter.Link.Client.SteerAsync(session.SessionId, text, cancellationToken);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException)
        {
            return null;
        }

        switch (steered.Outcome)
        {
            case "injected":
                lock (_gate)
                {
                    if (_turn == turn)
                        Apply(new TurnSteered(Now(), text));
                }
                _threads.Touch(_threadId);
                return turn.RunId;
            case "promptRequired":
                // The turn ended as the message went: it starts the next one, once this one's session is closed.
                await turn.Done;
                return Send(text);
            default:
                return null;
        }
    }

    /// <summary>
    /// Chooses <paramref name="value"/> for the thread's config option <paramref name="configId"/>. Every session the
    /// thread opens from now on gets it, and the session open now, if any, gets it at once.
    /// </summary>
    public async Task SetConfigAsync(string configId, string value, CancellationToken cancellationToken)
    {
        _threads.SetConfigChoice(_threadId, configId, value);
        OpenSession? session;
        lock (_gate)
            session = _turn?.Session ?? _kept?.Session;
        if (session is null)
            return;
        try
        {
            var set = await session.Adapter.Link.Client.SetConfigOptionAsync(session.SessionId, configId, value, cancellationToken);
            _threads.SetConfigOptions(_threadId, set.ConfigOptions.GetRawText());
        }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException)
        {
            // The session is ending or refused it; the choice is kept for the next one.
            Console.Error.WriteLine($"thread {_threadId}: {configId}={value} wasn't set on the open session: {error.Message}");
        }
    }

    public async Task<PermissionOutcome> DecideAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        var options = request.Options.Select(option => new AskOption(option.OptionId, option.Name, option.Kind)).ToList();
        var card = new AskPayload("permission", "open", request.ToolTitle, null, ToolCallId(request.ToolCall), options, null, null);
        var answer = await AskAsync(card, cancellationToken);
        return PermissionOutcome.Selected(answer.GetProperty("optionId").GetString()!);
    }

    public async Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken cancellationToken)
    {
        var card = new AskPayload("question", "open", null, request.Message, request.ToolCallId, null, request.RequestedSchema.Clone(), null);
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
            _ = CancelQuietlyAsync(session);
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
            return (await session.Adapter.Link.Client.StopTaskAsync(session.SessionId, taskId, cancellationToken)).Stopped;
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
        Func<long, AgentEvent> end;
        try
        {
            if (session is null)
            {
                await _session.WaitAsync(turn.Stopping.Token);
                held = true;
                session = await _opener.OpenAsync(_threadId, text, turn.Stages, turn.Stopping.Token);
                updates = session.Adapter.Updates.For(session.SessionId);
                session.Adapter.Link.Client.SetSessionPolicy(session.SessionId, this);
                lock (_gate)
                {
                    turn.Session = session;
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
                var response = await session.Adapter.Link.Client.PromptAsync(session.SessionId, text, CancellationToken.None);
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
                Apply(end(Now()));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"thread {_threadId}: the turn's end wasn't saved: {error.Message}");
            }
            finally
            {
                // Even if saving the end failed, the thread takes sends again.
                sessionCost = _converter.SessionCost;
                _turn = null;
                // Background work still going keeps the session, and with it the work, alive.
                var working = _tasks.Count > 0 || _subagents.AnyRunning || _helpersWorking();
                keep = session is not null && updates is not null && !lost && !_shuttingDown && (working || toolsOpen);
                if (keep)
                    _kept = new KeptSession(session!, updates!, pump, ticking, ticks, TasksDueBy: working ? 0 : Now() + TaskGraceMs);
            }
        }

        _turnEnded?.Invoke(_threadId);
        if (keep)
        {
            _log.WriteLine($"thread {_threadId} run {turn.RunId}: {turn.Stages}");
            _log.WriteLine($"thread {_threadId}: session kept open for background work");
            return;
        }

        if (session is not null)
            await CloseAsync(session, turn.Stages, sessionCost, lost, pump);
        if (held)
            _session.Release();
        _log.WriteLine($"thread {_threadId} run {turn.RunId}: {turn.Stages}");
        await ticking.CancelAsync();
        await ticks;
        ticking.Dispose();
    }

    /// <summary>Closes a session that was kept for background work, once that work and any run on it are done.</summary>
    private async Task CloseKeptAsync(KeptSession kept, bool lost)
    {
        decimal? sessionCost;
        lock (_gate)
            sessionCost = _converter.SessionCost;
        await CloseAsync(kept.Session, stages: null, sessionCost, lost, kept.Pump);
        _session.Release();
        await kept.Ticking.CancelAsync();
        await kept.Ticks;
        kept.Ticking.Dispose();
        _log.WriteLine($"thread {_threadId}: kept session closed");
    }

    // Called with the lock held: a kept session with nothing left on it closes.
    private void CloseIfDone()
    {
        if (_kept is not { } kept || _turn is not null || _tasks.Count > 0 || _subagents.AnyRunning || _helpersWorking()
            || Now() < kept.TasksDueBy)
            return;
        _kept = null;
        _lastTurn = Task.Run(() => CloseKeptAsync(kept, lost: false));
    }

    // Called with the lock held.
    private void Track(TaskChanged task)
    {
        if (task.Running)
            _tasks[task.TaskId] = task;
        else if (_tasks.Remove(task.TaskId, out var started))
            // An end names only the task, so it keeps what its start said.
            task = task with { Name = task.Name.Length > 0 ? task.Name : started.Name, ToolCallId = task.ToolCallId ?? started.ToolCallId };
        else
            return;
        Apply(task);
    }

    // Called with the lock held: the agent says a cycle of its work is over.
    private void EndCycle(CycleEnded cycle)
    {
        // Claude's tag names no task, so its end ends all the untracked ones.
        if (cycle.EndsUntracked)
            foreach (var task in _tasks.Values.Where(task => !task.Tracked).ToList())
                Track(task with { At = cycle.At, State = "completed" });
        if (_turn is { SelfStarted: true })
            EndSelfRun(new TurnEnded(cycle.At, "end_turn", null));
    }

    // Called with the lock held.
    private void StartSelfRun(long at)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _turn = new RunningTurn(Guid.NewGuid().ToString(), new StageTimes(_time))
        {
            SelfStarted = true, Session = _kept!.Session, LastEventAt = at, Ended = ended, Done = ended.Task,
        };
        Apply(new RunStarted(at, _turn.RunId, "Continued after a background task finished"));
        _log.WriteLine($"thread {_threadId} run {_turn.RunId}: started by the agent");
    }

    // Called with the lock held.
    private void EndSelfRun(AgentEvent end)
    {
        _turn?.Ended?.TrySetResult();
        Apply(end);
        _turn = null;
    }

    /// <summary>
    /// Closes the session on the adapter, which ends its agent process, and saves the session's total after a clean
    /// close: the resumed session reports on from it. A lost adapter took the session with it. Background work the
    /// agent can stop is stopped first; whatever is left ends with the session.
    /// </summary>
    private async Task CloseAsync(OpenSession session, StageTimes? stages, decimal? sessionCost, bool lost, Task pump)
    {
        _sessionClosed?.Invoke(_threadId);
        try
        {
            if (!lost)
            {
                List<string> stoppable;
                lock (_gate)
                    stoppable = _tasks.Values.Where(task => task.CanStop).Select(task => task.TaskId).ToList();
                foreach (var taskId in stoppable)
                {
                    try
                    {
                        await session.Adapter.Link.Client.StopTaskAsync(session.SessionId, taskId, CancellationToken.None);
                    }
                    catch (JsonRpcException)
                    {
                        // Closing ends it anyway.
                    }
                }
                await session.Adapter.Link.Client.CloseSessionAsync(session.SessionId, CancellationToken.None);
                stages?.Mark("session close");
                if (sessionCost is { } total)
                    _threads.SetSessionCost(_threadId, total);
            }
        }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"thread {_threadId}: session {session.SessionId} didn't close: {error.Message}");
        }
        finally
        {
            session.Adapter.Link.Client.SetSessionPolicy(session.SessionId, null);
            session.Adapter.Updates.Forget(session.SessionId);
            await pump;
            lock (_gate)
            {
                foreach (var task in _tasks.Values.ToList())
                    Track(task with { At = Now(), State = "stopped" });
                foreach (var shown in _subagents.StopAll(Now()))
                    Apply(shown);
            }
            await session.Pool.ReleaseAsync();
        }
    }

    /// <summary>Applies the session's updates as they arrive, until it's closed.</summary>
    private async Task PumpAsync(ChannelReader<SessionNotification> updates)
    {
        try
        {
            while (await updates.WaitToReadAsync())
            {
                lock (_gate)
                    Drain(updates);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"thread {_threadId}: updates stopped: {error.Message}");
        }

        lock (_gate)
        {
            // A kept session's updates end only when its adapter does (closing it takes it out of _kept first).
            if (_kept is not { } kept || kept.Updates != updates)
                return;
            if (_turn is { SelfStarted: true })
                EndSelfRun(new AgentLost(Now()));
            _kept = null;
            _lastTurn = Task.Run(() => CloseKeptAsync(kept, lost: true));
        }
    }

    /// <summary>Sends a clock tick every coalescing window, so text the agent paused on still goes out.</summary>
    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(RowConverter.CoalesceMs), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                lock (_gate)
                {
                    Apply(new ClockTick(Now()));
                    EndIfQuiet();
                    CloseIfDone();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Called with the lock held: an agent that never says its cycle is over doesn't keep its run open for good.
    private void EndIfQuiet()
    {
        if (_turn is not { SelfStarted: true } turn || Now() - turn.LastEventAt < QuietMs || _converter.HasOpenTools || _tasks.Count > 0)
            return;
        _log.WriteLine($"thread {_threadId} run {turn.RunId}: no word from the agent for {QuietMs / 1000} s, so the run ends");
        EndSelfRun(new TurnEnded(Now(), "end_turn", null));
        CloseIfDone();
    }

    // Called with the lock held.
    private void Drain(ChannelReader<SessionNotification> updates)
    {
        while (updates.TryRead(out var update))
        {
            _turn?.Stages.Mark("first update");
            // A subagent's own traffic goes to its thread.
            if (_subagents.Has(update.SessionId))
            {
                if (_subagents.Apply(update, Now(), _converter.KnowsTool) is { } linked)
                    Apply(linked);
                continue;
            }

            foreach (var agentEvent in UpdateReader.ReadAll(update, Now()))
            {
                switch (agentEvent)
                {
                    case SubagentChanged subagent:
                        if (!_subagents.Has(subagent.SessionId))
                            // What the child asks (a permission, a question) is a card in this thread.
                            (_turn?.Session ?? _kept?.Session)?.Adapter.Link.Client.SetSessionPolicy(subagent.SessionId, this);
                        Apply(_subagents.Changed(_threads.Get(_threadId)!, subagent));
                        break;
                    case TitleChanged title when _threads.Get(_threadId) is { Helper: false }:
                        _threads.SetTitle(_threadId, title.Title);
                        break;
                    case ConfigOptionsChanged changed:
                        _threads.SetConfigOptions(_threadId, changed.Options.GetRawText());
                        break;
                    case CommandsChanged commands:
                        _threads.SetCommands(_threadId, commands.Commands.GetRawText());
                        break;
                    case TaskChanged task:
                        Track(task);
                        break;
                    case CycleEnded cycle:
                        EndCycle(cycle);
                        break;
                    default:
                        // New work the agent does with no prompt running, on a session kept for background work. A
                        // late update to an earlier tool call (Codex's backgrounded command finishing) isn't.
                        if (_turn is null && _kept is not null
                            && agentEvent is TextChunk or ThoughtChunk or PlanChanged or ToolCallChanged { Started: true })
                            StartSelfRun(agentEvent.At);
                        if (_turn is { SelfStarted: true } self)
                            self.LastEventAt = agentEvent.At;
                        Apply(agentEvent);
                        break;
                }
            }
        }

        CloseIfDone();
    }

    // Called with the lock held.
    /// <summary>A helper session's row in this thread, as it stands now.</summary>
    internal void Show(SubagentShown helper)
    {
        lock (_gate)
            Apply(helper);
    }

    private void Apply(AgentEvent agentEvent) => _hub.Publish(_threadId, _converter.Apply(agentEvent));

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private static async Task CancelQuietlyAsync(OpenSession session)
    {
        try
        {
            await session.Adapter.Link.Client.CancelAsync(session.SessionId, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The agent is gone, so the turn is ending anyway.
        }
    }
}
