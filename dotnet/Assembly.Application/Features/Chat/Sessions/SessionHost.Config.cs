using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Chat.Sessions;

// SessionHost, continued: the session's config choices.
public sealed partial class SessionHost
{
    private readonly SemaphoreSlim _configChanges = new(1, 1);
    /// <summary>Starts its own turn once the running turn ends, without steering it.</summary>
    public async Task<string?> SendAfterTurnAsync(string text, string? clientId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunningTurn? turn;
            lock (_gate)
            {
                if (clientId is not null && _hub.UserRowOfClient(_sessionId, clientId) is { } row)
                {
                    SameText(clientId, row.Text, text);
                    return row.RunId;
                }
                turn = _turn;
                // Send records the client id under this lock before any other waiter can send it.
                if (turn is null && Send(text, clientId) is { } runId)
                    return runId;
            }
            if (turn is not null)
                await turn.Done.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Chooses <paramref name="value"/> for <paramref name="configId"/> in every ACP session the session opens from now on.
    /// An open session gets it first, and it's stored only once that agent takes it: a cancel stores nothing, a refusal fails.
    /// A session still opening has already read the stored choices, so the choice waits for that open to finish.
    /// </summary>
    public async Task SetConfigAsync(string configId, string value, CancellationToken cancellationToken, string? targetId = null)
    {
        if (targetId is not null)
        {
            var info = _sessionStore.Get(_sessionId) ?? throw new CallFailed("unknown session");
            _opener.ValidateTarget(info.Agent, targetId);
        }
        Task? warming;
        lock (_gate) warming = _warm?.Task;
        if (warming is not null)
        {
            try { await warming.WaitAsync(cancellationToken); } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        OpenSession? session;
        RunningTurn? opening;
        lock (_gate)
        {
            session = _turn?.Session ?? _kept?.Session;
            opening = _turn is { Session: null } ? _turn : null;
        }

        if (opening is not null)
        {
            await opening.Opened.Task.WaitAsync(cancellationToken);
            await SetConfigAsync(configId, value, cancellationToken, targetId);
            return;
        }

        if (session is null)
        {
            if (targetId is not null)
            {
                await SetAndWarmConfigAsync(configId, value, targetId, cancellationToken);
                return;
            }
            _sessionStore.SetConfigChoice(_sessionId, configId, value);
            return;
        }
        await _configChanges.WaitAsync(cancellationToken);
        try
        {
            try
            {
                var info = _sessionStore.Get(_sessionId) ?? throw new CallFailed("unknown session");
                if (targetId is not null)
                {
                    _opener.ValidateTarget(info.Agent, targetId);
                    if (session.Adapter.TargetId != targetId) throw new CallFailed("The open agent configuration changed.");
                }
                var set = await session.Adapter.Link.Client.SetConfigOptionAsync(session.AcpSessionId, configId, value, cancellationToken);
                var previous = _sessionStore.Config(_sessionId);
                var choices = previous.Choices.ToDictionary(x => x.Key, x => x.Value);
                choices[configId] = value;
                if (targetId is not null && (session.Adapter.TargetId != targetId || ConfigOptions.Selects(set.ConfigOptions).FirstOrDefault(x => x.Id == configId).Current != value))
                    throw new CallFailed("The agent did not confirm the selected setting.");
                _sessionStore.ObserveConfig(info, session.AcpSessionId, set.ConfigOptions.GetRawText(), previous.Commands, choices,
                    () => { if (targetId is not null) _opener.ValidateTarget(info.Agent, targetId); }, cancellationToken);
            }
            catch (JsonRpcException error)
            {
                throw new CallFailed($"the agent didn't take {configId}={value}: {error.Message}");
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                if (targetId is not null) throw new CallFailed("The agent ended before confirming the selected setting.");
                // The session is ending; the choice is kept for the next one.
                _sessionStore.SetConfigChoice(_sessionId, configId, value);
                Console.Error.WriteLine($"session {_sessionId}: {configId}={value} wasn't set on the open session: {error.Message}");
            }
        }
        finally { _configChanges.Release(); }
    }
}
