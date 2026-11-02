using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Sessions;

/// <summary>
/// A session's ACP session, open on the shared adapter. <c>SavedCost</c> is the total its agent reported at its last
/// clean close. <c>Titled</c> is set when this open gave the session its first title, from the turn's message.
/// </summary>
internal sealed record OpenSession(SharedAdapter Pool, RunningAdapter Adapter, string AcpSessionId, bool Resumed, decimal? SavedCost, string? Model = null,
    bool Titled = false);

/// <summary>
/// Opens a session's ACP session on its agent's shared adapter, in the session's checkout: its earlier session,
/// resumed, when the adapter can resume one; otherwise a new session, whose id is saved for next time. A new session
/// is titled from its first message when the agent takes a title, so it doesn't spend a model call making one. Every
/// ACP session gets the session's chosen config values (model, effort, mode…) that its agent offers, in the agent's order
/// (a model's effort levels can depend on the model). The session holds its place on the adapter until
/// <see cref="SharedAdapter.ReleaseAsync"/>; its next open waits for an adapter retired by its timed-out close to exit. <paramref name="mcpServers"/> gives a session's ACP session the MCP servers it joins.
/// </summary>
public sealed class SessionOpener(SessionStore sessionStore, IAgentLauncher launcher, Func<string, SharedAdapter> adapters,
    Func<string, IReadOnlyList<JsonElement>>? mcpServers = null)
{
    /// <summary>The longest title a first message gives a new session.</summary>
    public const int TitleLength = 60;

    internal void ValidateTarget(string agent, string targetId)
    {
        if (string.IsNullOrEmpty(targetId)) throw new Assembly.Application.Common.Calls.CallFailed("Select the agent configuration again.");
        ConfigTargetIdentity.Capture(launcher, agent, targetId);
    }

    internal async Task<OpenSession> OpenAsync(string sessionId, string firstText, StageTimes stages, CancellationToken cancellationToken,
        bool configWarm = false, string? targetId = null, KeyValuePair<string, string>? pick = null, Action? validate = null)
    {
        var session = sessionStore.Get(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} no longer exists.");
        var config = sessionStore.Config(sessionId);
        var expectedTarget = targetId ?? config.ConfigTarget;
        var pendingInitial = session.AcpSessionId is null && config.ConfigTarget is not null;
        var captured = ConfigTargetIdentity.Capture(launcher, session.Agent, expectedTarget);
        var folder = configWarm ? sessionStore.Root(session) : await launcher.CheckoutAsync(sessionStore.Top(session), stages, cancellationToken);
        if (configWarm) launcher.ValidateFolder(folder ?? "");
        var adapter = adapters(session.Agent);
        var running = await adapter.GetForSessionAsync(session.AcpSessionId, stages, cancellationToken, captured.TargetId);
        string? nativeId = null;
        var resumed = false;
        try
        {
            JsonElement options = default;
            if (session.AcpSessionId is { } previous)
            {
                if (configWarm && !running.Init.CanResumeSessions) throw new Assembly.Application.Common.Calls.CallFailed("Settings unavailable: this adapter cannot resume the saved session.");
                if (running.Init.CanResumeSessions)
                {
                    try
                    {
                        var resuming = running.Link.Client.ResumeSessionAsync(previous, folder!, CancellationToken.None, configWarm ? null : mcpServers?.Invoke(sessionId));
                        var response = await AnsweredAsync(adapter, running, sessionId, resuming, _ => previous, cancellationToken, configWarm, previous);
                        nativeId = previous;
                        resumed = true;
                        options = response.ConfigOptions;
                        stages.Mark("session resume");
                    }
                    catch (JsonRpcException error)
                    {
                        if (configWarm) throw new Assembly.Application.Common.Calls.CallFailed($"Settings unavailable: {error.Message}");
                        // Existing normal-turn fallback only.
                    }
                }
            }
            if (nativeId is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var opening = running.Link.Client.NewSessionAsync(folder!, CancellationToken.None, configWarm ? null : mcpServers?.Invoke(sessionId));
                var response = await AnsweredAsync(adapter, running, sessionId, opening, x => x.SessionId, cancellationToken, configWarm, null);
                nativeId = response.SessionId;
                options = response.ConfigOptions;
                stages.Mark("session new");
            }

            // Stage every explicit initial choice and native ID until the entire map is accepted.
            var strict = pendingInitial;
            var choices = config.Choices.ToDictionary(x => x.Key, x => x.Value);
            var observed = ConfigObservation.Read(running.Updates, nativeId, options);
            var initialOptions = observed.RawOptions;
            if (strict && (ConfigOptions.Model(observed.Options) is not { } initialModel || !choices.ContainsKey(initialModel.Id)))
                throw new Assembly.Application.Common.Calls.CallFailed("The agent no longer offers the initial model selection.");
            observed = await ApplyChoicesAsync(running, nativeId, observed, choices, strict, stages, cancellationToken);
            if (pick is { } choosing)
            {
                observed = await ApplyChoicesAsync(running, nativeId, observed,
                    new Dictionary<string, string> { [choosing.Key] = choosing.Value }, true, stages, cancellationToken);
                choices[choosing.Key] = choosing.Value;
            }
            sessionStore.ObserveConfig(session, nativeId, observed.RawOptions, observed.Commands, pick is null ? null : choices, () =>
            {
                ConfigTargetIdentity.Capture(launcher, session.Agent, captured.TargetId);
                validate?.Invoke();
            }, cancellationToken, agentOptions: !resumed && !configWarm ? initialOptions : null);

            var title = !configWarm && !resumed && session.Title is null ? Title(firstText) : null;
            if (title is not null) sessionStore.SetTitle(sessionId, title, "prompt");
            if (!configWarm && running.Init.CanRenameSessions)
            {
                var agentTitle = resumed ? session.TitleSource == "helper" ? session.Title : null : session.Helper ? session.Title : Title(firstText);
                if (agentTitle is not null) await running.Link.Client.RenameSessionAsync(nativeId, agentTitle, cancellationToken);
            }
            return new OpenSession(adapter, running, nativeId, resumed, resumed ? session.SessionCost : null,
                ConfigOptions.ModelName(observed.Options), title is not null);
        }
        catch (Exception error)
        {
            if (nativeId is not null)
            {
                running.Updates.Forget(nativeId);
                if (configWarm) running.Link.Client.SetSessionPolicy(nativeId, null);
                if (!configWarm) await CloseQuietlyAsync(adapter, running, nativeId);
            }
            if (configWarm || pendingInitial) await adapter.ReleaseWarmAsync(running, nativeId ?? session.AcpSessionId);
            else await adapter.ReleaseAsync(running);
            if (configWarm && error is JsonRpcException rpc) throw new Assembly.Application.Common.Calls.CallFailed($"Settings unavailable: {rpc.Message}");
            if (configWarm && error is IOException or ObjectDisposedException) throw new Assembly.Application.Common.Calls.CallFailed("The agent ended before its settings were available.");
            throw;
        }
    }

    private static async Task<T> AnsweredAsync<T>(SharedAdapter adapter, RunningAdapter running, string sessionId,
        Task<T> opening, Func<T, string> id, CancellationToken ct, bool warm, string? previous)
    {
        try { return await opening.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                var late = id(await opening.WaitAsync(SharedAdapter.CloseLimit, adapter.Time, CancellationToken.None));
                running.Updates.Forget(late);
                if (!warm) await CloseQuietlyAsync(adapter, running, late);
            }
            catch (TimeoutException)
            {
                // The unanswered open may own the transcript. Fence the exact process until it exits.
                await adapter.RetireAsync(running, previous);
            }
            catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException) { }
            throw;
        }
    }

    private static async Task CloseQuietlyAsync(SharedAdapter adapter, RunningAdapter running, string nativeId)
    {
        try { await adapter.CloseAsync(running, nativeId); }
        catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException or TimeoutException)
        { Console.Error.WriteLine($"session {nativeId} didn't close after a failed open: {error.Message}"); }
    }

    private static async Task<ConfigObservation> ApplyChoicesAsync(RunningAdapter running, string nativeId, ConfigObservation observed,
        IReadOnlyDictionary<string, string> choices, bool strict, StageTimes stages, CancellationToken ct)
    {
        // Model first; subsequent option values/defaults come from its actual returned snapshot.
        var applied = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var modelId = ConfigOptions.Model(observed.Options)?.Id;
            var id = ConfigOptions.Selects(observed.Options).OrderBy(x => x.Id == modelId ? 0 : 1)
                .Select(x => x.Id).FirstOrDefault(id => choices.ContainsKey(id) && !applied.Contains(id));
            if (id is null) break;
            applied.Add(id);
            var value = choices[id];
            var option = ConfigOptions.Selects(observed.Options).FirstOrDefault(x => x.Id == id);
            if (option.Values is null || !option.Values.Contains(value))
            {
                if (strict) throw new Assembly.Application.Common.Calls.CallFailed($"The agent no longer offers {id}={value}.");
                continue;
            }
            if (value == option.Current) continue;
            try
            {
                var set = await running.Link.Client.SetConfigOptionAsync(nativeId, id, value, ct);
                observed = ConfigObservation.Read(running.Updates, nativeId, set.ConfigOptions, observed.Commands);
            }
            catch (JsonRpcException error)
            {
                if (strict) throw new Assembly.Application.Common.Calls.CallFailed($"The agent didn't take {id}={value}: {error.Message}");
                Console.Error.WriteLine($"session {nativeId}: the agent didn't take {id}={value} ({error.Message})");
            }
        }
        if (strict && choices.Any(choice => ConfigOptions.Selects(observed.Options).FirstOrDefault(x => x.Id == choice.Key).Current != choice.Value))
            throw new Assembly.Application.Common.Calls.CallFailed("The agent did not confirm all selected settings.");
        if (choices.Count > 0) stages.Mark("session config");
        return observed;
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
