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
                var started = new TurnStarted(Now(), turn.RunId, text, clientId, senderLabel, fromSessionId);
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
                _lastTurn = turn.Done = Task.Run(() => RunAsync(turn, text, kept), CancellationToken.None);
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
                sending = InjectAsync(turn, session, text, clientId, senderLabel, admission, ct, fromSessionId);
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

    private async Task<SteerDeliveryResult> InjectAsync(RunningTurn turn, OpenSession session, string text, string? clientId, string? label, OwningSendAdmission? admission, CancellationToken ct, string? fromSessionId = null)
    {
        if (admission is not null && !CurrentAdmission(admission, text, clientId, label, fromSessionId)) throw new CallFailed("The delivery owner changed.");
        var outcome = await session.Adapter.Link.Client.SteerAsync(session.AcpSessionId, [TextBlock.Of(text)], ct);
        if (outcome != SteerOutcome.Injected) return new(outcome == SteerOutcome.PromptRequired ? SteerResult.PromptRequired : SteerResult.NotRunning);
        Assembly.Application.Common.Engine.UserRowReference? userRow = null;
        lock (_gate)
        {
            var batch = _converter.Apply(new TurnSteered(Now(), turn.RunId, text, clientId, label, fromSessionId));
            var saved = _hub.Publish(_sessionId, batch);
            var user = saved.Single(value => value.Kind == "user");
            if (turn.Observation is { } observed) userRow = new(user.Seq, turn.RunId, observed.Epoch);
        }
        _sessionStore.Touch(_sessionId);
        return new(SteerResult.Injected, userRow);
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


}
