using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;
using Assembly.Application.Common.Database;
using Assembly.Application.Common.Engine;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed partial class SessionHost
{
    public Task<SendResult> SendAsync(string text, string? clientId, string? senderLabel, CancellationToken ct, string? fromSessionId = null,
        IReadOnlyList<string>? attachmentIds = null) => SendAsync(text, clientId, senderLabel, null, ct, fromSessionId, attachmentIds);

    internal async Task<SendResult> SendAsync(string text, string? clientId, string? senderLabel, OwningSendAdmission? admission, CancellationToken ct, string? fromSessionId = null,
        IReadOnlyList<string>? attachmentIds = null)
    {
        ct.ThrowIfCancellationRequested();
        attachmentIds ??= [];
        if (admission is not null && !admission.AttachmentIds.SequenceEqual(attachmentIds)) return new SendResult.Unknown();
        if (admission?.PriorEffectId is not null) await WaitForPriorAuthTurn(admission, ct);
        if (admission is not null && !CurrentAdmission(admission, text, clientId, senderLabel, fromSessionId)) return new SendResult.Unknown();
        Task<SendResult> sending;
        var owns = false;
        WarmRun? supersededWarm = null;
        Exception? publicationError = null;
        lock (_gate)
        {
            if (_shuttingDown) return new SendResult.Unknown();
            if (clientId is not null && _sending.TryGetValue(clientId, out var active))
            {
                SameContent(clientId, active.Text, active.FromSessionId, active.Label, text, fromSessionId, senderLabel);
                if (active.EffectId != admission?.EffectId || !active.AttachmentIds.SequenceEqual(attachmentIds)) return new SendResult.Unknown();
                sending = active.Send;
            }
            else
            {
                var row = clientId is null ? null : _hub.UserDeliveryOfClient(_sessionId, clientId);
                if (row is not null)
                {
                    SameContent(clientId!, row.Text, row.From, row.SenderLabel, text, fromSessionId, senderLabel);
                    if (row.Acknowledgment is { Kind: "prompt_inserted", ProviderMessageId: { } providerId } && row.RunId is { } runId)
                        return new SendResult.Inserted(row.RetryExecution?.ChatRunId ?? runId, providerId, admission?.UserRow, row.RetryExecution);
                    if (admission is null || !MayReuse(admission, row)) return new SendResult.Unknown();
                    if (row.RetryExecution?.EffectId == admission.EffectId) return new SendResult.Unknown();
                }
                if (admission?.ExpiresMs <= Now()) return new SendResult.Failed("expired");
                if (_turn is not null) return new SendResult.Busy();
                var turn = new RunningTurn(Guid.NewGuid().ToString(), new StageTimes(_time)) { Delivery = admission };
                PrepareObservation(turn, Now());
                if (admission is not null && (turn.Observation is not { } observedStart ||
                    observedStart.SessionId != admission.SessionId || observedStart.Epoch != admission.Epoch))
                { turn.Stopping.Dispose(); return new SendResult.Unknown(); }
                if (row is not null)
                    turn.RetryExecution = new(turn.RunId, admission!.MessageId, admission.EffectId, admission.Attempt,
                        admission.PriorEffectId!, admission.ClientId, admission.Epoch, row.Seq, row.RunId!);
                var kept = _kept;
                var started = new TurnStarted(Now(), turn.RunId, text, clientId, senderLabel, fromSessionId,
                    UserItemId: row is null ? Guid.NewGuid().ToString() : null, AttachmentIds: attachmentIds.ToArray(), FromMachine: admission?.FromMachine);
                var committed = false;
                try
                {
                    _converter.CommitStarted(started, batch =>
                    {
                        if (row is not null) batch.Rows.RemoveAll(value => value.Kind == "user");
                        return _hub.Publish(_sessionId, batch, scope =>
                        {
                            scope.AfterCommit(() => committed = true);
                            if (turn.Observation is not { } observed) return;
                            turn.UserRow = row is not null ? admission!.UserRow : StartedUser(scope, turn.RunId, observed.Epoch);
                            if (turn.RetryExecution is { } execution) _feed!.RetryStarted(scope, observed, execution);
                        });
                    }, () => committed);
                }
                catch (Exception error) when (committed) { publicationError = error; }
                catch (CallFailed error) when (error.Reason == "expired" && turn.RetryExecution is not null)
                { turn.Stopping.Dispose(); return new SendResult.Failed("expired"); }
                catch { turn.Stopping.Dispose(); throw; }
                _kept = null;
                turn.Session = kept?.Session;
                Interlocked.Increment(ref _warmGeneration);
                supersededWarm = _warm;
                _turn = turn;
                sending = turn.Inserted.Task;
                if (clientId is not null) _sending[clientId] = (text, senderLabel, fromSessionId, sending, admission?.EffectId, turn.RunId, attachmentIds.ToArray());
                var execution = Task.Run(() => RunAsync(turn, text, kept), CancellationToken.None);
                _lastTurn = turn.Done = PublishReleasedAsync(turn, execution);
                if (admission is not null) _lastDelivery = (admission, turn.UserRow, turn.Done);
                owns = true;
            }
        }
        try
        {
            if (supersededWarm is not null)
            {
                try { await supersededWarm.Stop.CancelAsync(); } catch (ObjectDisposedException) { }
            }
            if (publicationError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(publicationError).Throw();
            if (owns) _sessionStore.Touch(_sessionId);
            return await sending.WaitAsync(ct);
        }
        finally
        {
            if (owns && clientId is not null && _sending.TryGetValue(clientId, out var active) && ReferenceEquals(active.Send, sending))
                _sending.TryRemove(new(clientId, active));
        }
    }

    // The task is captured before a later turn can replace it. This owned completion joins on shutdown.
    private async Task PublishReleasedAsync(RunningTurn original, Task<bool> execution)
    {
        if (await execution) PublishReleased(original);
    }

    private void PublishReleased(RunningTurn original)
    {
        if (_feed is null || original.Observation is not { } observed) return;
        try { _feed.Released(observed, original.RunId); }
        catch (Exception error)
        {
            // A failed terminal/release write cannot manufacture positive release or change auth retry completion.
            _log.WriteLine($"session {_sessionId} run {original.RunId}: execution release wasn't saved: {error.Message}");
        }
    }

    private async Task WaitForPriorAuthTurn(OwningSendAdmission admission, CancellationToken ct)
    {
        Task? done = null;
        lock (_gate)
            if (_lastDelivery is { } previous && previous.Delivery.EffectId == admission.PriorEffectId &&
                previous.Delivery.ClientId == admission.ClientId && previous.User == admission.UserRow)
                done = previous.Done;
        if (done is null) return;
        await done.WaitAsync(ct);
        lock (_gate)
            if (_sending.TryGetValue(admission.ClientId, out var old) && old.EffectId == admission.PriorEffectId && old.Send.IsCompleted)
                _sending.TryRemove(new(admission.ClientId, old));
    }

    private UserRowReference? StartedUser(IReadScope scope, string runId, long epoch)
    {
        var rows = scope.Query("SELECT seq FROM rows WHERE session_id=$session AND run_id=$run AND kind='user' AND item_id IS NULL ORDER BY seq LIMIT 2",
            r => r.Long(0), new SqlArg("$session", _sessionId), new("$run", runId));
        return rows.Count == 1 ? new(rows[0], runId, epoch) : null;
    }

    private bool MayReuse(OwningSendAdmission admission, UserDeliveryRow row) =>
        admission.SessionId == _sessionId && admission.ClientId == row.ClientId && admission.Text == row.Text &&
        admission.FromSessionId == row.From && admission.SenderLabel == row.SenderLabel && admission.UserRow?.Seq == row.Seq && admission.UserRow.RunId == row.RunId &&
        row.Acknowledgment is null && admission.UserRow?.Epoch == admission.Epoch &&
        _feed?.Capture(_sessionId)?.Epoch == admission.Epoch;

    private bool CurrentAdmission(OwningSendAdmission admission, string text, string? clientId, string? label, string? fromSessionId) =>
        admission.SessionId == _sessionId && admission.ClientId == clientId && admission.Text == text && admission.SenderLabel == label && admission.FromSessionId == fromSessionId &&
        (admission.UserRow is null) == (admission.PriorEffectId is null) && _feed?.Capture(_sessionId)?.Epoch == admission.Epoch;

    public async Task<SteerResult> SteerAsync(string text, string? clientId, string? senderLabel, CancellationToken ct, string? fromSessionId = null) =>
        (await SteerDeliveryAsync(text, clientId, senderLabel, null, ct, fromSessionId)).Outcome;

    internal async Task<SteerDeliveryResult> SteerDeliveryAsync(string text, string? clientId, string? senderLabel, OwningSendAdmission? admission, CancellationToken ct, string? fromSessionId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (admission is not null && !CurrentAdmission(admission, text, clientId, senderLabel, fromSessionId)) throw new CallFailed("The delivery owner changed.");
        Task<SteerDeliveryResult> sending;
        var owns = false;
        lock (_gate)
        {
            if (clientId is not null && _steering.TryGetValue(clientId, out var active))
            {
                SameContent(clientId, active.Text, active.FromSessionId, active.Label, text, fromSessionId, senderLabel); sending = active.Send;
            }
            else
            {
                var row = clientId is null ? null : _hub.UserDeliveryOfClient(_sessionId, clientId);
                if (row is not null)
                {
                    SameContent(clientId!, row.Text, row.From, row.SenderLabel, text, fromSessionId, senderLabel);
                    if (row.Acknowledgment?.Kind == "steer_injected") return new(SteerResult.Injected);
                    throw new CallFailed("The delivery outcome is unknown.");
                }
                var turn = _turn;
                if (turn is null) return new(SteerResult.NotRunning);
                if (turn.Session is not { } session || !session.Adapter.Init.CanSteer) return new(SteerResult.PromptRequired);
                if (admission is not null && !CurrentAdmission(admission, text, clientId, senderLabel, fromSessionId)) throw new CallFailed("The delivery owner changed.");
                sending = SettleSteerAsync(SendSteer(session, text, clientId, senderLabel, fromSessionId, admission));
                if (clientId is not null) _steering[clientId] = (text, senderLabel, fromSessionId, sending);
                owns = true;
            }
        }
        try { return await sending.WaitAsync(ct); }
        finally
        {
            if (owns && clientId is not null)
                lock (_gate)
                    if (_steering.TryGetValue(clientId, out var active) && ReferenceEquals(active.Send, sending)) _steering.Remove(clientId);
        }
    }

    private void SameContent(string clientId, string sent, string? from, string? capturedLabel, string text, string? fromSessionId, string? label)
    {
        if (sent == text && from == fromSessionId && capturedLabel == label) return;
        _log.WriteLine($"Warning: session {_sessionId}: clientId {clientId} was sent again with other text");
        throw new CallFailed("clientId already used for another message");
    }

    internal string? LegacyRunOfClient(string? clientId, string text, string? label, string? fromSessionId = null)
    {
        if (clientId is null) return null;
        lock (_gate)
        {
            var row = _hub.UserDeliveryOfClient(_sessionId, clientId);
            if (row is null) return null;
            SameContent(clientId, row.Text, row.From, row.SenderLabel, text, fromSessionId, label);
            return row.RetryExecution?.ChatRunId ?? row.RunId;
        }
    }

    internal (string? RunId, Task Done) CurrentTurn
    {
        get { lock (_gate) return (_turn?.RunId, _turn?.Done ?? _lastTurn); }
    }

    internal async Task StopCurrentAsync(CancellationToken ct)
    {
        Task done;
        lock (_gate)
        {
            if (_turn is not { } turn) return;
            done = turn.Done;
            Stop(turn.RunId);
        }
        await done.WaitAsync(ct);
    }

    // Messages sent into a running turn. The agent takes one at a point in its stream, where the reader records it;
    // a message it refuses goes back to the sender to be sent as the next prompt.

    /// <summary>A message sent to the agent that it has neither taken nor refused yet.</summary>
    private sealed class PendingSteer(SteerRun run, string text, string? clientId, string? label, string? fromSessionId,
        IReadOnlyList<string>? attachmentIds, string? fromMachine)
    {
        public SteerRun Run { get; } = run;
        public string Text { get; } = text;
        public string? ClientId { get; } = clientId;
        public string? Label { get; } = label;
        public string? FromSessionId { get; } = fromSessionId;
        public IReadOnlyList<string>? AttachmentIds { get; } = attachmentIds;
        public string? FromMachine { get; } = fromMachine;
        public string ItemId { get; } = Guid.NewGuid().ToString();
        /// <summary>Set once the reader has saved the message's row; faulted if it never will be.</summary>
        public TaskCompletionSource<UserRowReference?> Recorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Guarded by the host's lock.
        public RunningTurn? Turn { get; set; }
        public long? KeptSince { get; set; }
        public long? CancelSentAt { get; set; }
    }

    // The messages the open ACP session's agent has yet to take or refuse, by steer id. Guarded by _gate.
    private readonly Dictionary<string, PendingSteer> _steers = [];
    // Set while the reader waits at a prompted turn's end for that turn to be finalized. Guarded by _gate.
    private TaskCompletionSource? _readerPaused;

    // Called with the lock held, so finalization's keep decision sees the message or the message sees no turn.
    private PendingSteer SendSteer(OpenSession session, string text, string? clientId, string? label, string? fromSessionId,
        OwningSendAdmission? admission)
    {
        var run = session.Adapter.Link.Client.Steer(session.AcpSessionId, [TextBlock.Of(text)], CancellationToken.None);
        var steer = new PendingSteer(run, text, clientId, label, fromSessionId, admission?.AttachmentIds.ToArray(), admission?.FromMachine);
        _steers[run.Id] = steer;
        return steer;
    }

    /// <summary>
    /// The message's outcome: refused by the agent, or taken and recorded. Anything else (a fault, the session or
    /// adapter ending first) leaves it uncertain, and the wait throws.
    /// </summary>
    private async Task<SteerDeliveryResult> SettleSteerAsync(PendingSteer steer)
    {
        if (await Task.WhenAny(steer.Run.Delivered, steer.Recorded.Task) == steer.Run.Delivered
            && steer.Run.Delivered is not { IsCompletedSuccessfully: true, Result: SteerOutcome.Injected })
        {
            // Refused, or its request failed: the agent will never take it, so the session doesn't wait on it. A
            // failure stays uncertain and is thrown.
            lock (_gate)
            {
                _steers.Remove(steer.Run.Id);
                CloseIfDone();
            }
            await steer.Run.Delivered;
            return new(SteerResult.PromptRequired);
        }
        var row = await steer.Recorded.Task;
        _sessionStore.Touch(_sessionId);
        return new(SteerResult.Injected, row);
    }

    // Called with the lock held, by the reader: the agent took a message here. It goes in the run open now, or starts
    // the next one on a kept session.
    private PendingSteer? Insert(SteerInserted inserted, RowBatch batch, ref IReadOnlyList<AgentEvent> facts)
    {
        if (!_steers.TryGetValue(inserted.SteerId, out var steer)) return null;
        if (_turn is null && _kept is not null)
        {
            StartSelfRun(inserted.At, batch);
            facts = [.. facts, new TurnStarted(inserted.At, _turn!.RunId, "")];
        }
        if (_turn is not { } turn) return null;
        if (turn.SelfStarted) turn.LastEventAt = inserted.At;
        Apply(new TurnSteered(inserted.At, turn.RunId, steer.Text, steer.ClientId, steer.Label, steer.FromSessionId,
            steer.ItemId, steer.AttachmentIds, steer.FromMachine), batch);
        steer.Turn = turn;
        return steer;
    }

    // Called with the lock held, once the batch with the message's row is saved.
    private void Recorded(PendingSteer steer, IReadOnlyList<StoredRow> saved)
    {
        _steers.Remove(steer.Run.Id);
        var row = saved.FirstOrDefault(value => value.Kind == "user" && value.Payload.TryGetProperty("itemId", out var id)
            && id.GetString() == steer.ItemId);
        steer.Recorded.TrySetResult(row is null || steer.Turn?.Observation is not { } observed
            ? null : new UserRowReference(row.Seq, steer.Turn.RunId, observed.Epoch));
    }

    // Called with the lock held: these messages will never be recorded on this session.
    private void FaultSteers(Exception error)
    {
        foreach (var steer in _steers.Values) Fail(steer, error);
        _steers.Clear();
    }

    private static void Fail(PendingSteer steer, Exception error)
    {
        steer.Recorded.TrySetException(error);
        _ = steer.Recorded.Task.Exception;
    }

    // Called with the lock held: the session was kept with these messages waiting on it.
    private void MarkKept()
    {
        var now = Now();
        foreach (var steer in _steers.Values) steer.KeptSince ??= now;
    }

    // Called with the lock held: the reader goes on past a prompted turn's end.
    private void ResumeReader()
    {
        _readerPaused?.TrySetResult();
        _readerPaused = null;
    }

    // Called with the lock held, by the ticker: a kept session waits on a message for at most two quiet periods, a
    // cancel after the first and a close after the second, and the close itself is limited by the adapter.
    private void BoundSteers()
    {
        if (_kept is not { } kept || _steers.Count == 0) return;
        var now = Now();
        if (_steers.Values.Any(steer => steer.CancelSentAt is { } sent && now - sent >= QuietMs))
        {
            ForceCloseKept(kept);
            return;
        }
        if (_turn is not null || !_steers.Values.Any(steer => steer.CancelSentAt is null && steer.KeptSince is { } since && now - since >= QuietMs))
            return;
        foreach (var steer in _steers.Values) steer.CancelSentAt ??= now;
        _log.WriteLine($"session {_sessionId}: the agent hasn't taken a waiting message for {QuietMs / 1000} s, so it's cancelled");
        _work.Start("cancel waiting message " + _sessionId, stopping => CancelQuietlyAsync(kept.Session, stopping));
    }

    // Called with the lock held. The run on the session ends and the messages fault before the close is awaited, so
    // nothing here waits on the agent.
    private void ForceCloseKept(KeptSession kept)
    {
        _log.WriteLine($"session {_sessionId}: the agent never settled a waiting message, so its session closes");
        try
        {
            if (_turn is { SelfStarted: true } self)
            {
                try { EndSelfRun(new TurnEnded(Now(), "cancelled", null)); }
                finally
                {
                    _turn = null;
                    self.Ended?.TrySetResult();
                }
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"session {_sessionId}: the run's end wasn't saved: {error.Message}");
        }
        finally
        {
            FaultSteers(new IOException("The agent neither took nor refused the message, so its session was closed."));
            _kept = null;
            _lastTurn = Task.Run(() => CloseKeptAsync(kept, lost: false));
        }
    }
}
