using System.Text.Json;
using System.Threading.Channels;
using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Acp.V2;
using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Domain.Sessions;

using Assembly.Application.Common.Agents;
namespace Assembly.Application.Features.Chat.Sessions;

// SessionHost, continued: reading the session's updates, the background work they tell of, and closing the session.
public sealed partial class SessionHost
{
    private UpdateReader _updateReader = new();
    private string? _readerSessionId;
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
        _log.WriteLine($"session {_sessionId}: kept session closed");
    }

    // Called with the lock held: a kept session with nothing left on it closes.
    private void CloseIfDone()
    {
        if (_kept is not { } kept || _turn is not null || _tasks.Count > 0 || _subagents.AnyRunning || _startedWorking()
            || Now() < kept.TasksDueBy)
            return;
        _kept = null;
        _lastTurn = Task.Run(() => CloseKeptAsync(kept, lost: false));
    }

    // Called with the lock held.
    private void Track(TaskChanged task, RowBatch? collector = null)
    {
        if (task.Running)
            _tasks[task.TaskId] = task;
        else if (_tasks.Remove(task.TaskId, out var started))
            // An end names only the task, so it keeps what its start said.
            task = task with { Name = task.Name.Length > 0 ? task.Name : started.Name, ToolCallId = task.ToolCallId ?? started.ToolCallId };
        else
            return;
        Apply(task, collector);
    }

    // Called with the lock held: the agent says a cycle of its work is over.
    private void EndCycle(CycleEnded cycle, RowBatch? collector = null)
    {
        // Claude's tag names no task, so its end ends all the untracked ones.
        if (cycle.EndsUntracked)
            foreach (var task in _tasks.Values.Where(task => !task.Tracked).ToList())
                Track(task with { At = cycle.At, State = "completed" }, collector);
        if (_turn is { SelfStarted: true })
            EndSelfRun(new TurnEnded(cycle.At, "end_turn", null), collector);
    }

    // Called with the lock held.
    private void StartSelfRun(long at, RowBatch? collector = null)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _turn = new RunningTurn(Guid.NewGuid().ToString(), new StageTimes(_time))
        {
            SelfStarted = true,
            Session = _kept!.Session,
            LastEventAt = at,
            Ended = ended,
            Done = ended.Task,
        };
        PrepareObservation(_turn, at);
        if (_kept is { } kept) BindPolicy(kept.Session, _turn);
        Apply(new RunStarted(at, _turn.RunId, "Continued after a background task finished"), collector);
        _log.WriteLine($"session {_sessionId} run {_turn.RunId}: started by the agent");
    }

    // Called with the lock held.
    private void EndSelfRun(AgentEvent end, RowBatch? collector = null)
    {
        _turn?.Ended?.TrySetResult();
        Apply(end, collector);
        _turn = null;
    }

    /// <summary>
    /// Closes the session on the adapter, which ends its agent process, and saves the session's total after a clean
    /// close: the resumed session reports on from it. A lost adapter took the session with it. Background work the
    /// agent can stop is stopped first; whatever is left ends with the session.
    /// </summary>
    private async Task CloseAsync(OpenSession session, StageTimes? stages, decimal? sessionCost, bool lost, Task pump)
    {
        _sessionClosed?.Invoke(_sessionId);
        try
        {
            if (!lost)
            {
                await session.Pool.CloseAsync(session.Adapter, session.AcpSessionId);
                stages?.Mark("session close");
                if (sessionCost is { } total)
                    _sessionStore.SetSessionCost(_sessionId, total);
            }
        }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException or TimeoutException)
        {
            Console.Error.WriteLine($"session {_sessionId}: session {session.AcpSessionId} didn't close: {error.Message}");
        }
        finally
        {
            session.Adapter.Link.Client.SetSessionPolicy(session.AcpSessionId, null);
            session.Adapter.Updates.Forget(session.AcpSessionId);
            await pump;
            lock (_gate)
            {
                _failedStarts.Clear();
                foreach (var task in _tasks.Values.ToList())
                    Track(task with { At = Now(), State = "stopped" });
                foreach (var shown in _subagents.StopAll(Now()))
                    Apply(shown);
            }
            await session.Pool.ReleaseAsync(session.Adapter);
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
            Console.Error.WriteLine($"session {_sessionId}: updates stopped: {error.Message}");
        }

        lock (_gate)
        {
            // A kept session's updates end only when its adapter does (closing it takes it out of _kept first).
            if (_kept is not { } kept || kept.Updates != updates)
                return;
            _hub.Publish(_sessionId, new RowBatch(), kept.Observation is not { } observed ? null :
                scope => _feed?.Lost(scope, observed, "provider_gone"));
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
        _log.WriteLine($"session {_sessionId} run {turn.RunId}: no word from the agent for {QuietMs / 1000} s, so the run ends");
        EndSelfRun(new TurnEnded(Now(), "end_turn", null));
        CloseIfDone();
    }

    // Called with the lock held.
    private void Drain(ChannelReader<SessionNotification> updates)
    {
        while (updates.TryRead(out var update))
        {
            _turn?.Stages.Mark("first update");
            var events = _updateReader.Read(update, Now());
            var batch = new RowBatch();
            var observed = _turn?.Observation;
            var fallback = _kept?.Observation;
            RunningTurn? endedTurn = null;
            AgentEvent? terminal = null;
            foreach (var agentEvent in events)
            {
                switch (agentEvent)
                {
                    case TurnStarted:
                        if (_turn is null && _kept is not null) StartSelfRun(agentEvent.At, batch);
                        observed ??= _turn?.Observation;
                        break;
                    case TurnEnded or TurnFailed:
                        if (_turn is { SelfStarted: true } selfEnding)
                        {
                            endedTurn = selfEnding; terminal = agentEvent;
                            EndSelfRun(agentEvent, batch);
                        }
                        break;
                    case SessionMessage:
                        // The host already recorded this prompt; user_message is its provider acknowledgement.
                        break;
                    case ProviderSessionMessage:
                        // Provider-native traffic is observed provenance, not an Assembly delivery or another user prompt.
                        break;
                    case SubagentChanged subagent:
                        // What the child asks (a permission, a question) AcpClient sends to this session's policy, as its parent's.
                        Apply(_subagents.Changed(_sessionStore.Get(_sessionId)!, subagent), batch);
                        break;
                    case TitleChanged title when _sessionStore.Get(_sessionId) is { Helper: false }:
                        _sessionStore.SetTitle(_sessionId, title.Title, "prompt");
                        break;
                    case ConfigOptionsChanged changed:
                        _sessionStore.SetConfigOptions(_sessionId, changed.Options.GetRawText());
                        break;
                    case CommandsChanged commands:
                        _sessionStore.SetCommands(_sessionId, commands.Commands.GetRawText());
                        break;
                    case TaskChanged task:
                        Track(task, batch);
                        break;
                    case CycleEnded cycle:
                        EndCycle(cycle, batch);
                        break;
                    default:
                        // New work the agent does with no prompt running, on a session kept for background work. A
                        // late update to an earlier tool call (Codex's backgrounded command finishing) isn't.
                        if (_turn is null && _kept is not null
                            && agentEvent is TextChunk or ThoughtChunk or PlanChanged or ToolCallChanged { Started: true })
                            StartSelfRun(agentEvent.At, batch);
                        if (_turn is { SelfStarted: true } self)
                            self.LastEventAt = agentEvent.At;
                        observed ??= _turn?.Observation;
                        Apply(agentEvent, batch);
                        break;
                }
            }
            if (_turn is { SelfStarted: false } prompted && events.Any(e => e is TurnEnded or TurnFailed))
            {
                prompted.TerminalObserved = true;
                prompted.TerminalCost = _converter.SessionCost;
            }
            CommitReaderBatch(batch, observed ?? fallback, update.SessionId, events, endedTurn, terminal);
        }

        CloseIfDone();
    }
}
