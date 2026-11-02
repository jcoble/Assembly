using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Events;

namespace Assembly.Application.Features.Chat.Sessions;

public sealed partial class SessionHost
{
    public Task<SendResult> SendAsync(string text, string? clientId, string? senderLabel, CancellationToken ct) =>
        SendAsync(text, clientId, senderLabel, null, ct);

    internal async Task<SendResult> SendAsync(string text, string? clientId, string? senderLabel, OwningSendAdmission? admission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (admission is not null && !CurrentAdmission(admission, text, clientId, senderLabel)) return new SendResult.Unknown();
        Task<SendResult> sending;
        var owns = false;
        lock (_gate)
        {
            if (clientId is not null && _sending.TryGetValue(clientId, out var active))
            {
                SameContent(clientId, active.Text, active.Label, text, senderLabel);
                sending = active.Send;
            }
            else
            {
                var row = clientId is null ? null : _hub.UserDeliveryOfClient(_sessionId, clientId);
                if (row is not null)
                {
                    SameContent(clientId!, row.Text, row.From, text, senderLabel);
                    if (row.Acknowledgment is { Kind: "prompt_inserted", ProviderMessageId: { } providerId } && row.RunId is { } runId)
                        return new SendResult.Inserted(runId, providerId);
                    if (admission is null || !MayReuse(admission, row)) return new SendResult.Unknown();
                }
                if (_turn is not null) return new SendResult.Busy();
                (var kept, _kept) = (_kept, null);
                var turn = new RunningTurn(row?.RunId ?? Guid.NewGuid().ToString(), new StageTimes(_time)) { Session = kept?.Session, Delivery = admission };
                PrepareObservation(turn, Now());
                var started = new TurnStarted(Now(), turn.RunId, text, clientId, senderLabel);
                var batch = _converter.Apply(started);
                if (row is not null) batch.Rows.RemoveAll(value => value.Kind == "user");
                var saved = _hub.Publish(_sessionId, batch);
                _turn = turn;
                var user = saved.FirstOrDefault(value => value.Kind == "user");
                if (turn.Observation is { } observed)
                    turn.UserRow = row is not null ? admission!.UserRow : user is null ? null : new(user.Seq, turn.RunId, observed.Epoch);
                _sessionStore.Touch(_sessionId);
                sending = turn.Inserted.Task;
                if (clientId is not null) _sending[clientId] = (text, senderLabel, sending);
                _lastTurn = turn.Done = Task.Run(() => RunAsync(turn, text, kept), CancellationToken.None);
                owns = true;
            }
        }
        try { return await sending.WaitAsync(ct); }
        finally
        {
            if (owns && clientId is not null)
                lock (_gate)
                    if (_sending.TryGetValue(clientId, out var active) && ReferenceEquals(active.Send, sending)) _sending.Remove(clientId);
        }
    }

    private bool MayReuse(OwningSendAdmission admission, UserDeliveryRow row) =>
        admission.SessionId == _sessionId && admission.ClientId == row.ClientId && admission.Text == row.Text &&
        admission.SenderLabel == row.From && admission.UserRow?.Seq == row.Seq && admission.UserRow.RunId == row.RunId &&
        row.Acknowledgment is null && admission.UserRow?.Epoch == admission.Epoch &&
        _feed?.Capture(_sessionId)?.Epoch == admission.Epoch;

    private bool CurrentAdmission(OwningSendAdmission admission, string text, string? clientId, string? label) =>
        admission.SessionId == _sessionId && admission.ClientId == clientId && admission.Text == text && admission.SenderLabel == label &&
        (admission.UserRow is null) == (admission.PriorEffectId is null) && _feed?.Capture(_sessionId)?.Epoch == admission.Epoch;

    public async Task<SteerResult> SteerAsync(string text, string? clientId, string? senderLabel, CancellationToken ct) =>
        (await SteerDeliveryAsync(text, clientId, senderLabel, null, ct)).Outcome;

    internal async Task<SteerDeliveryResult> SteerDeliveryAsync(string text, string? clientId, string? senderLabel, OwningSendAdmission? admission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (admission is not null && !CurrentAdmission(admission, text, clientId, senderLabel)) throw new CallFailed("The delivery owner changed.");
        Task<SteerDeliveryResult> sending;
        var owns = false;
        lock (_gate)
        {
            if (clientId is not null && _steering.TryGetValue(clientId, out var active))
            {
                SameContent(clientId, active.Text, active.Label, text, senderLabel); sending = active.Send;
            }
            else
            {
                var row = clientId is null ? null : _hub.UserDeliveryOfClient(_sessionId, clientId);
                if (row is not null)
                {
                    SameContent(clientId!, row.Text, row.From, text, senderLabel);
                    if (row.Acknowledgment?.Kind == "steer_injected") return new(SteerResult.Injected);
                    throw new CallFailed("The delivery outcome is unknown.");
                }
                var turn = _turn;
                if (turn is null) return new(SteerResult.NotRunning);
                if (turn.Session is not { } session || !session.Adapter.Init.CanSteer) return new(SteerResult.PromptRequired);
                sending = InjectAsync(turn, session, text, clientId, senderLabel, admission, ct);
                if (clientId is not null) _steering[clientId] = (text, senderLabel, sending);
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

    private async Task<SteerDeliveryResult> InjectAsync(RunningTurn turn, OpenSession session, string text, string? clientId, string? label, OwningSendAdmission? admission, CancellationToken ct)
    {
        if (admission is not null && !CurrentAdmission(admission, text, clientId, label)) throw new CallFailed("The delivery owner changed.");
        var outcome = await session.Adapter.Link.Client.SteerAsync(session.AcpSessionId, [TextBlock.Of(text)], ct);
        if (outcome != SteerOutcome.Injected) return new(outcome == SteerOutcome.PromptRequired ? SteerResult.PromptRequired : SteerResult.NotRunning);
        Assembly.Application.Common.Engine.UserRowReference? userRow = null;
        lock (_gate)
        {
            var batch = _converter.Apply(new TurnSteered(Now(), turn.RunId, text, clientId, label));
            var saved = _hub.Publish(_sessionId, batch);
            var user = saved.Single(value => value.Kind == "user");
            if (turn.Observation is { } observed) userRow = new(user.Seq, turn.RunId, observed.Epoch);
        }
        _sessionStore.Touch(_sessionId);
        return new(SteerResult.Injected, userRow);
    }

    private void SameContent(string clientId, string sent, string? from, string text, string? label)
    {
        if (sent == text && from == label) return;
        _log.WriteLine($"Warning: session {_sessionId}: clientId {clientId} was sent again with other text");
        throw new CallFailed("clientId already used for another message");
    }

    internal string? LegacyRunOfClient(string? clientId, string text, string? label)
    {
        if (clientId is null) return null;
        lock (_gate)
        {
            var row = _hub.UserDeliveryOfClient(_sessionId, clientId);
            if (row is null) return null;
            SameContent(clientId, row.Text, row.From, text, label);
            return row.RunId;
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

    internal bool AnswerProviderRequest(string chatRowId, string providerRequestId, System.Text.Json.JsonElement answer)
    {
        lock (_gate) return _asks.TryGetValue(chatRowId, out var ask) &&
            ask.Request?.ProviderRequestId == providerRequestId && Answer(chatRowId, answer);
    }
}
