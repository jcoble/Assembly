using Assembly.Application.Common.Acp;
using Assembly.Application.Common.Agents;
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
            var turn = CurrentTurn;
            var sent = await SendAsync(text, clientId, null, cancellationToken);
            switch (sent)
            {
                case SendResult.Inserted inserted: return inserted.RunId;
                case SendResult.Unknown: return LegacyRunOfClient(clientId, text, null);
                case SendResult.Busy:
                    await turn.Done.WaitAsync(cancellationToken); continue;
                case SendResult.AuthRequired: throw new CallFailed("authentication required");
                case SendResult.Failed failed: throw new CallFailed(failed.Reason);
            }
        }
    }

    /// <summary>
    /// Chooses <paramref name="value"/> for <paramref name="configId"/> in every ACP session the session opens from now on.
    /// An open session gets it first, and it's stored only once that agent takes it: a cancel stores nothing, a refusal fails.
    /// A session still opening has already read the stored choices, so the choice waits for that open to finish.
    /// </summary>
    public async Task SetConfigAsync(string configId, string value, CancellationToken cancellationToken, string? targetId = null)
    {
        OpenSession? session;
        RunningTurn? opening;
        Task? warming;
        lock (_gate)
        {
            warming = _warm?.Task;
            session = _turn?.Session ?? _kept?.Session;
            opening = _turn is { Session: null } ? _turn : null;
        }

        if (warming is not null)
        {
            await warming.WaitAsync(cancellationToken);
            await SetConfigAsync(configId, value, cancellationToken, targetId);
            return;
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
        Action? validate = null;
        if (targetId is not null)
        {
            var info = _sessionStore.Get(_sessionId) ?? throw new CallFailed("unknown session");
            var location = _sessionStore.Location(info);
            var config = _sessionStore.Config(_sessionId);
            var captured = ConfigTargetIdentity.Capture(_opener.Launcher, info.Agent, targetId);
            if (session.Adapter.TargetId != captured.TargetId) throw new CallFailed("The open agent configuration changed.");
            var generation = Interlocked.Read(ref _warmGeneration);
            validate = () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConfigTargetIdentity.Validate(_opener.Launcher, captured.Descriptor, captured.Environment);
                var current = _sessionStore.Get(_sessionId);
                var actual = _sessionStore.Config(_sessionId);
                if (Interlocked.Read(ref _warmGeneration) != generation || current is null || current.Agent != info.Agent ||
                    current.Machine != info.Machine || current.AcpSessionId != info.AcpSessionId || _sessionStore.Location(current) != location ||
                    actual.ConfigTarget != config.ConfigTarget || !actual.Choices.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .SequenceEqual(config.Choices.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
                    throw new CallFailed("The session changed while its settings were being read.");
            };
            validate();
        }
        System.Text.Json.JsonElement options;
        try
        {
            options = (await session.Adapter.Link.Client.SetConfigOptionAsync(session.AcpSessionId, configId, value, cancellationToken)).ConfigOptions;
        }
        catch (JsonRpcException error)
        {
            if (targetId is not null) throw new CallFailed("The agent did not accept the selected setting.");
            throw new CallFailed($"the agent didn't take {configId}={value}: {error.Message}");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            if (targetId is not null) throw new CallFailed("The agent ended before the setting was confirmed.");
            // The session is ending; the choice is kept for the next one without confirming model intent.
            _sessionStore.SetUnconfirmedConfigChoice(_sessionId, configId, value);
            Console.Error.WriteLine($"session {_sessionId}: {configId}={value} wasn't set on the open session: {error.Message}");
            return;
        }
        if (targetId is not null && !ConfigOptions.Selects(options).Any(option => option.Id == configId && option.Current == value))
            throw new CallFailed("The agent did not confirm the selected setting.");
        _sessionStore.ConfirmModelSelection(_sessionId, configId, value, options.GetRawText(), validate);
    }
}
