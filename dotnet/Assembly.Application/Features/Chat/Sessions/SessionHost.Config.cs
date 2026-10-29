using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Chat.Sessions;

// SessionHost, continued: the session's config choices.
public sealed partial class SessionHost
{
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
    public async Task SetConfigAsync(string configId, string value, CancellationToken cancellationToken)
    {
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
            await SetConfigAsync(configId, value, cancellationToken);
            return;
        }

        if (session is null)
        {
            _sessionStore.SetConfigChoice(_sessionId, configId, value);
            return;
        }
        try
        {
            var set = await session.Adapter.Link.Client.SetConfigOptionAsync(session.AcpSessionId, configId, value, cancellationToken);
            _sessionStore.SetConfigChoice(_sessionId, configId, value);
            _sessionStore.SetConfigOptions(_sessionId, set.ConfigOptions.GetRawText());
        }
        catch (JsonRpcException error)
        {
            throw new CallFailed($"the agent didn't take {configId}={value}: {error.Message}");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The session is ending; the choice is kept for the next one.
            _sessionStore.SetConfigChoice(_sessionId, configId, value);
            Console.Error.WriteLine($"session {_sessionId}: {configId}={value} wasn't set on the open session: {error.Message}");
        }
    }
}
