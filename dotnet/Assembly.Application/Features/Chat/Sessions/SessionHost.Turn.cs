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
using Assembly.Application.Common.Agents.Auth;
namespace Assembly.Application.Features.Chat.Sessions;

// The existing owning turn/open/close lifecycle.
public sealed partial class SessionHost
{
    private async Task<bool> RunAsync(RunningTurn turn, string text, KeptSession? kept)
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
                turn.OpenAttempt = new AuthOpenAttempt();
                session = await _opener.OpenAsync(_sessionId, text, turn.Stages, turn.Stopping.Token,
                    observation: turn.Observation, authAttempt: turn.OpenAttempt, validate: () =>
                    {
                        if (turn.Delivery is { } opening && !CurrentAdmission(opening, text, opening.ClientId, opening.SenderLabel, opening.FromSessionId))
                            throw new CallFailed("The delivery owner changed.", "delivery_owner_changed");
                    });
                updates = session.Adapter.Updates.For(session.AcpSessionId);
                BindPolicy(session, turn);
                lock (_gate)
                {
                    // The previous pump has completed before the session semaphore is released. Switch the
                    // reader under the same gate used to drain updates, before starting this session's pump.
                    if (_readerSessionId != session.AcpSessionId)
                    {
                        _updateReader = new UpdateReader();
                        _readerSessionId = session.AcpSessionId;
                    }
                    turn.Session = session;
                    RememberHandoffExecution(turn);
                    _titlePending |= session.Titled;
                    turn.Opened.TrySetResult();
                    turn.Cost.AgentStarted(session.Resumed, session.SavedCost);
                    Apply(new AgentStarted(Now(), session.Resumed, session.SavedCost, session.Model),
                        observe: turn.Observation is not { } observed ? null : scope =>
                            _feed?.Connected(scope, observed, session.AcpSessionId, session.Adapter.Init.CanSteer));
                }

                pump = PumpAsync(updates);
            }
            if (kept is not null)
            {
                BindPolicy(session, turn);
                lock (_gate) RememberHandoffExecution(turn);
            }
            if (turn.Delivery is { } delivery && !CurrentAdmission(delivery, text, delivery.ClientId, delivery.SenderLabel, delivery.FromSessionId))
            {
                turn.Inserted.TrySetResult(new SendResult.Unknown());
                throw new CallFailed("The delivery owner changed.");
            }
            if (turn.Delivery?.ExpiresMs <= Now())
            {
                turn.Inserted.TrySetResult(new SendResult.Failed("expired"));
                throw new CallFailed("expired", "expired");
            }
            // A stop that lands between here and the prompt reaching the agent is lost; the turn then runs to its end.
            if (turn.Stopping.IsCancellationRequested)
            {
                end = at => new TurnEnded(at, "cancelled", null);
            }
            else
            {
                turn.Prompt = session.Adapter.Link.Client.PromptAsync(session.AcpSessionId, [TextBlock.Of(text)], CancellationToken.None);
                PromptResult response;
                try
                {
                    turn.ProviderMessageId = await turn.Prompt.Inserted;
                    turn.Inserted.TrySetResult(new SendResult.Inserted(turn.RunId, turn.ProviderMessageId, turn.UserRow, turn.RetryExecution));
                    response = await turn.Prompt.Ended;
                }
                catch
                {
                    // Both tasks can fail on the same pre-insertion error; observe both without sending again.
                    try { await turn.Prompt.Ended; } catch { }
                    throw;
                }
                if (response.MessageId != turn.ProviderMessageId)
                    throw new InvalidOperationException("The provider prompt identity changed.");
                if (response.Usage?.CostUsd is { } terminalCost)
                {
                    turn.TerminalObserved = true;
                    turn.TerminalCost = terminalCost;
                }
                turn.Stages.Mark("answer");
                end = at => response.Error is { } failure
                    ? new TurnFailed(at, failure.Message, failure.Code.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : UpdateReader.TurnEnd(response, at);
            }
        }
        catch (OperationCanceledException) when (turn.Stopping.IsCancellationRequested)
        {
            end = at => new TurnEnded(at, "cancelled", null);
        }
        catch (JsonRpcException error)
        {
            turn.AuthRequired = error.Code == -32000;
            if (!turn.AuthRequired) turn.Inserted.TrySetResult(new SendResult.Failed(error.Message));
            end = at => UpdateReader.TurnFailure(error, at);
        }
        catch (IOException) when (session is not null)
        {
            // The pipe closed under the prompt: the adapter is gone, whether or not its exit has been seen yet.
            // Its process group is killed as it exits, and the next send starts a new adapter.
            lost = true;
            turn.Inserted.TrySetResult(new SendResult.Unknown());
            end = at => new AgentLost(at);
        }
        catch (Exception error)
        {
            turn.Inserted.TrySetResult(error is CallFailed { Reason: "delivery_owner_changed" } ? new SendResult.Unknown() :
                error is CallFailed { Reason: { } reason } ? new SendResult.Failed(reason) :
                turn.Prompt is null ? new SendResult.Failed(error.Message) : new SendResult.Unknown());
            end = at => new TurnFailed(at, error.Message, null);
        }

        if (!turn.AuthRequired) turn.Inserted.TrySetResult(new SendResult.Unknown());
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
                if (!lost && !_shuttingDown)
                    foreach (var task in _updateReader.RetainOutstanding(ended.At))
                        Track(task);
                var completed = false;
                var proof = FinalAuthTerminal(turn, session, ended);
                long terminalSeq = 0;
                if (turn.AuthTerminalSeq == 0)
                    Apply(ended, observe: turn.Observation is not { } observed ? null : scope =>
                    {
                        if (proof is not null) terminalSeq = ReportAuthTerminal(scope, turn, proof);
                        if (session is not null) _feed?.ObserveBatch(scope, observed, session.AcpSessionId, [ended]);
                        completed = CompleteFeed(scope, turn, session, ended);
                        ReplyFeed(scope, turn, session, ended);
                    }, authProof: proof);
                if (terminalSeq != 0) turn.AuthTerminalSeq = terminalSeq;
                if (completed) turn.FeedCompleted = true;
                // A refusal is safe to complete only after its real terminal/report/reply writer succeeds.
                // Otherwise the worker could finalize E1 before Report has its owning inflight authority.
                if (turn.AuthRequired) turn.Inserted.TrySetResult(new SendResult.AuthRequired(turn.UserRow, turn.RetryExecution));
                title = _titlePending && ended is TurnEnded { StopReason: not "cancelled" };
                if (title)
                    _titlePending = false;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"session {_sessionId}: the turn's end wasn't saved: {error.Message}");
            }
            finally
            {
                turn.Inserted.TrySetResult(new SendResult.Unknown());
                // Even if saving the end failed, the session takes sends again.
                sessionCost = _converter.SessionCost;
                _turn = null;
                turn.Opened.TrySetResult();
                // Background work still going keeps the session, and with it the work, alive.
                var working = _tasks.Count > 0 || _subagents.AnyRunning || _startedWorking();
                keep = session is not null && updates is not null && !lost && !_shuttingDown && (working || toolsOpen);
                if (keep)
                    _kept = new KeptSession(session!, updates!, pump, ticking, ticks, TasksDueBy: working ? 0 : Now() + TaskGraceMs, turn.Observation, turn);
            }
        }

        _turnEnded?.Invoke(_sessionId);
        if (title && _titler is { } titler)
            _work.Start($"title {_sessionId}", stopping => titler.TitleAsync(_sessionId, text, turn.RunId, stopping));
        if (keep)
        {
            _log.WriteLine($"session {_sessionId} run {turn.RunId}: {turn.Stages}");
            _log.WriteLine($"session {_sessionId}: session kept open for background work");
            return false;
        }

        if (session is not null)
            await CloseAsync(session, turn.Stages, sessionCost, lost, pump);
        if (held)
            _session.Release();
        _log.WriteLine($"session {_sessionId} run {turn.RunId}: {turn.Stages}");
        await ticking.CancelAsync();
        await ticks;
        ticking.Dispose();
        MarkHandoffExecutionCompleted(turn, clean: !lost);
        return true;
    }

}
