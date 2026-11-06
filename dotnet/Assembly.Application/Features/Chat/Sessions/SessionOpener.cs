using Assembly.Application.Features.Chat.Sessions.Mcp;
using Assembly.Application.Common.Engine;
using Assembly.Application.Common.Acp;
using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Agents.Auth;
using Assembly.Application.Common.Calls;
using Assembly.Domain.Sessions;
using Assembly.Application.Features.Chat.Sessions.Configuration;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// A session's ACP session, open on the shared adapter. <c>SavedCost</c> is the total its agent reported at its last
/// clean close. <c>Titled</c> is set when this open gave the session its first title, from the turn's message.
/// </summary>
internal sealed record OpenSession(SharedAdapter Pool, RunningAdapter Adapter, string AcpSessionId, bool Resumed, decimal? SavedCost, string? Model = null,
    bool Titled = false, string? Folder = null);

/// <summary>
/// Opens a session's ACP session on its agent's shared adapter, in the session's checkout: its earlier session,
/// resumed, when the adapter can resume one; otherwise a new session, whose id is saved for next time. A new session
/// is titled from its first message when the agent takes a title, so it doesn't spend a model call making one. Every
/// ACP session gets the session's chosen config values (model, effort, mode…) that its agent offers, in the agent's order
/// (a model's effort levels can depend on the model). The session holds its place on the adapter until
/// <see cref="SharedAdapter.ReleaseAsync"/>; its next open waits for an adapter retired by its timed-out close to exit. <paramref name="mcp"/> gives a session's ACP session the MCP servers it joins.
/// </summary>
public sealed class SessionOpener(SessionStore sessionStore, IAgentLauncher launcher, Func<string, SharedAdapter> adapters,
    SessionMcp? mcp = null)
{
    internal Native.NativeResume Native { get; } = new(sessionStore, launcher, adapters);

    /// <summary>The longest title a first message gives a new session.</summary>
    public const int TitleLength = 60;
    internal IAgentLauncher Launcher => launcher;

    internal async Task<OpenSession> OpenAsync(string sessionId, string firstText, StageTimes stages, CancellationToken cancellationToken,
        SessionObservation? observation = null, AuthOpenAttempt? authAttempt = null, bool configWarm = false,
        string? targetId = null, KeyValuePair<string, string>? pick = null, Action? validate = null, string? strictResumeId = null)
    {
        if (!configWarm && mcp is not null) await mcp.WaitReady(cancellationToken);
        var session = sessionStore.Get(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} no longer exists.");
        validate?.Invoke();
        if (strictResumeId is not null && (strictResumeId.Length == 0 || session.AcpSessionId != strictResumeId))
            throw new CallFailed("The accepted public session identity changed.", "handoff_execution_changed");
        var config = sessionStore.Config(sessionId);
        if (targetId is not null && (targetId.Length == 0 || config.ConfigTarget is { } stored && stored != targetId))
            throw new CallFailed("Select the agent configuration again.");
        var captured = ConfigTargetIdentity.Capture(launcher, session.Agent, targetId ?? config.ConfigTarget);
        var pendingInitial = session.AcpSessionId is null && config.ConfigTarget is not null;
        var location = sessionStore.Location(session) ?? throw new CallFailed($"This session has no working folder: {sessionId}");
        if (!location.Ready)
            throw new CallFailed("The checkout and session start are not ready.", "checkout_not_ready");
        var folder = location.Folder;
        launcher.ValidateFolder(folder);
        var adapter = adapters(session.Agent);
        var running = await adapter.GetForSessionAsync(session.AcpSessionId, stages, cancellationToken, captured.TargetId,
            suppliedDescriptor: captured.Descriptor with { Env = captured.Environment },
            initializeAuthRequired: authAttempt is null ? null : authAttempt.InitializeRejected);
        void Validate()
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigTargetIdentity.Validate(launcher, captured.Descriptor, captured.Environment);
            validate?.Invoke();
            if (!configWarm) mcp?.ValidateOpen(sessionId, observation);
        }
        string? acceptedNativeId = session.AcpSessionId;
        try
        {
            return await OpenOnAsync(adapter, running, session, folder, firstText, stages, cancellationToken, observation, authAttempt,
                config, location, configWarm, pendingInitial, pick, Validate, id => acceptedNativeId = id,
                id => NativeLaunchContext.Capture(captured.Descriptor, captured.Environment, captured.TargetId, id, folder));
        }
        catch (Exception error)
        {
            if (!configWarm) mcp?.Revoke(sessionId);
            if (configWarm || pendingInitial) await adapter.ReleaseWarmAsync(running, acceptedNativeId);
            else await adapter.ReleaseAsync(running);
            if (configWarm && strictResumeId is null && error is JsonRpcException rpc)
                throw rpc.Error.IsAuthRequired
                    ? new CallFailed("Sign in to this provider to read its settings.", "authentication_required")
                    : new CallFailed("The agent could not restore this session's settings.", "settings_unavailable");
            if (configWarm && strictResumeId is null && error is IOException or ObjectDisposedException)
                throw new CallFailed("The agent ended before its settings were available.", "settings_unavailable");
            throw;
        }
    }

    /// <summary>Restores exactly the accepted public ACP session, with no fallback, prompt or replay request.</summary>
    internal async Task<string> ResumeForHandoffAsync(string sessionId, string expectedAcpSessionId, StageTimes stages,
        CancellationToken ct, Action validate)
    {
        if (string.IsNullOrEmpty(expectedAcpSessionId)) throw new CallFailed("The accepted public session identity is missing.");
        OpenSession? opened = null;
        try
        {
            // The existing detached warm-open path never falls back from resume to new and installs no MCP servers.
            opened = await OpenAsync(sessionId, "", stages, ct, configWarm: true, validate: validate, strictResumeId: expectedAcpSessionId);
            validate();
            return opened.AcpSessionId;
        }
        finally
        {
            if (opened is not null)
            {
                await opened.Pool.ReleaseWarmAsync(opened.Adapter, opened.AcpSessionId);
                await opened.Adapter.Link.Exited.WaitAsync(ct);
                await opened.Adapter.Updates.Completion.WaitAsync(ct);
            }
        }
    }

    private async Task<OpenSession> OpenOnAsync(
        SharedAdapter adapter, RunningAdapter running, SessionInfo session, string folder, string firstText, StageTimes stages,
        CancellationToken cancellationToken, SessionObservation? observation, AuthOpenAttempt? authAttempt,
        SessionConfig config, SessionLocation location, bool configWarm, bool pendingInitial,
        KeyValuePair<string, string>? pick, Action validate, Action<string> nativeAccepted, Func<string, NativeSessionLaunch?> nativeLaunch)
    {
        var sessionId = session.SessionId;
        var detached = configWarm || pendingInitial;
        if (session.AcpSessionId is { } previous)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The same ACP id on the same warm adapter, so the closed turn's late updates share it. The router opens it
            // again at the answer, in pipe order (UpdateRouter), not when this await resumes on another thread.
            var resuming = running.Link.Client.ResumeSessionAsync(new(previous, folder, configWarm ? [] : mcp?.ServersFor(sessionId, observation) ?? []), CancellationToken.None);
            ResumeSessionResponse? resumed = null;
            try
            {
                resumed = await AnsweredAsync(adapter, running, sessionId, resuming, _ => previous, cancellationToken, authAttempt, detached, previous, nativeAccepted);
                stages.Mark("session resume");
                var model = await ApplyChoicesAsync(running, session, config, location, previous, resumed.ConfigOptions,
                    resumed.AvailableCommands, stages, cancellationToken, pendingInitial, pick, validate, saveAgentOptions: false, nativeLaunch(previous));
                if (!configWarm) mcp?.ValidateOpen(sessionId, observation);
                return new OpenSession(adapter, running, previous, Resumed: true, session.SessionCost, model, Folder: folder);
            }
            catch (JsonRpcException error) when (resumed is null && error.Code == -32000)
            {
                authAttempt?.ResumeSessionRejected();
                throw;
            }
            catch (JsonRpcException) when (resumed is null && configWarm)
            {
                // A restored metadata read never falls back to a different native transcript.
                throw;
            }
            catch (JsonRpcException error) when (resumed is null)
            {
                mcp?.Revoke(sessionId);
                // The agent lost its transcript. A new ACP session keeps the session usable; the agent won't remember it.
                Console.Error.WriteLine($"session {sessionId}: could not resume {previous} ({error.Message}); starting a new session");
            }
            catch (Exception) when (resumed is not null)
            {
                // Stopped or failed after the resume answered: the agent has the session open, so it's closed there
                // too, and anything it sends for it from now on is dropped.
                if (!configWarm) mcp?.Revoke(sessionId);
                running.Updates.Forget(previous);
                if (configWarm) running.Link.Client.SetSessionPolicy(previous, null);
                else await CloseQuietlyAsync(adapter, running, previous);
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var opening = running.Link.Client.NewSessionAsync(new(folder, configWarm ? [] : mcp?.ServersFor(sessionId, observation) ?? []), CancellationToken.None);
        NewSessionResponse acp;
        try { acp = await AnsweredAsync(adapter, running, sessionId, opening, late => late.SessionId, cancellationToken, authAttempt, detached, null, nativeAccepted); }
        catch (JsonRpcException error) when (error.Code == -32000) { authAttempt?.NewSessionRejected(); throw; }

        try
        {
            stages.Mark("session new");
            var chosen = await ApplyChoicesAsync(running, session, config, location, acp.SessionId, acp.ConfigOptions,
                acp.AvailableCommands, stages, cancellationToken, pendingInitial, pick, validate, saveAgentOptions: !configWarm, nativeLaunch(acp.SessionId));
            var title = !configWarm && session.Title is null ? Title(firstText) : null;
            if (title is not null)
                sessionStore.SetTitle(sessionId, title, "prompt");
            if (!configWarm) mcp?.ValidateOpen(sessionId, observation);
            return new OpenSession(adapter, running, acp.SessionId, Resumed: false, SavedCost: null, chosen, Titled: title is not null, Folder: folder);
        }
        catch (Exception)
        {
            // Stopped or failed once the agent had opened it: it's closed there too, as after a resume.
            if (!configWarm) mcp?.Revoke(sessionId);
            running.Updates.Forget(acp.SessionId);
            if (configWarm) running.Link.Client.SetSessionPolicy(acp.SessionId, null);
            else await CloseQuietlyAsync(adapter, running, acp.SessionId);
            throw;
        }
    }

    // Neither adapter honours a request's cancel, so a session/new or session/resume goes out on a token the cancel
    // doesn't cut: a cancel after it went out waits for the answer and closes the session the agent opened. That delays
    // the cancel by up to twice the close limit: the wait for the answer, then the close.
    private async Task<T> AnsweredAsync<T>(
        SharedAdapter adapter, RunningAdapter running, string sessionId, Task<T> opening, Func<T, string> acpSessionId,
        CancellationToken cancellationToken, AuthOpenAttempt? authAttempt, bool detached, string? previous, Action<string> nativeAccepted)
    {
        try
        {
            var accepted = await opening.WaitAsync(cancellationToken);
            authAttempt?.AcceptedIdentity();
            nativeAccepted(acpSessionId(accepted));
            return accepted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!detached) mcp?.Revoke(sessionId);
            try
            {
                var accepted = await opening.WaitAsync(SharedAdapter.CloseLimit, adapter.Time, CancellationToken.None);
                authAttempt?.AcceptedIdentity();
                var late = acpSessionId(accepted);
                nativeAccepted(late);
                running.Updates.Forget(late);
                if (detached) running.Link.Client.SetSessionPolicy(late, null);
                else await CloseQuietlyAsync(adapter, running, late);
            }
            catch (TimeoutException)
            {
                if (detached) await adapter.RetireAsync(running, previous);
            }
            catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException)
            {
                // No answer in time, or none at all: a session it opened lives until the adapter exits. An error opened none.
            }
            throw;
        }
    }

    private static async Task CloseQuietlyAsync(SharedAdapter adapter, RunningAdapter running, string acpSessionId)
    {
        try
        {
            await adapter.CloseAsync(running, acpSessionId);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException or TimeoutException)
        {
            Console.Error.WriteLine($"session {acpSessionId} didn't close after a failed open: {error.Message}");
        }
    }

    /// <summary>Sets each chosen value the session doesn't have yet, saves the options as they end up, and returns their model's name.</summary>
    private async Task<string?> ApplyChoicesAsync(RunningAdapter running, SessionInfo session, SessionConfig config,
        SessionLocation location, string nativeId, JsonElement options, JsonElement commands, StageTimes stages,
        CancellationToken ct, bool pendingInitial, KeyValuePair<string, string>? pick, Action validate, bool saveAgentOptions, NativeSessionLaunch? nativeLaunch)
    {
        var initial = ConfigObservation.Read(running.Updates, nativeId, options,
            commands.ValueKind == JsonValueKind.Array ? commands.GetRawText() : null);
        var collected = await ConfigChoiceCollector.CollectAsync(running, nativeId, initial, config, pendingInitial, pick, stages, ct);
        sessionStore.ObserveConfig(session, config, location, nativeId, collected.Observation.RawOptions,
            collected.Observation.Commands, collected.Choices, collected.ConfirmedModel, validate, ct,
            agentOptions: saveAgentOptions ? initial.RawOptions : null, nativeLaunch: nativeLaunch);
        return ConfigOptions.ModelName(collected.Observation.Options);
    }

    /// <summary>The message's first line, cut to <see cref="TitleLength"/> characters; null when it has no text.</summary>
    public static string? Title(string text)
    {
        var line = string.Join(' ', text.Trim().Split('\n', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0)
            return null;
        return line.Length <= TitleLength ? line : line[..(TitleLength - 1)].TrimEnd() + "…";
    }
}
