using System.Text.Json;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Application.Common.Acp.Messages;
using Assembly.Application.Common.Agents;
using Assembly.Application.Common.Calls;
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

    internal async Task<OpenSession> OpenAsync(string sessionId, string firstText, StageTimes stages, CancellationToken cancellationToken)
    {
        var session = sessionStore.Get(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} no longer exists.");
        var location = sessionStore.Location(session) ?? throw new CallFailed($"This session has no working folder: {sessionId}");
        string folder;
        if (location.ManagedCheckout)
        {
            if (!string.Equals(location.Folder, sessionStore.ManagedFolder(session), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new CallFailed($"This session's managed checkout does not match its working folder: {location.Folder}");
            folder = await launcher.CheckoutAsync(sessionStore.Top(session), stages, cancellationToken);
        }
        else
        {
            folder = location.Folder;
            launcher.ValidateFolder(folder);
        }
        var adapter = adapters(session.Agent);
        await adapter.ClosedAsync(session.AcpSessionId, cancellationToken);
        var running = await adapter.GetAsync(stages, cancellationToken);
        try
        {
            return await OpenOnAsync(adapter, running, session, folder, firstText, stages, cancellationToken);
        }
        catch
        {
            await adapter.ReleaseAsync(running);
            throw;
        }
    }

    private async Task<OpenSession> OpenOnAsync(
        SharedAdapter adapter, RunningAdapter running, SessionInfo session, string folder, string firstText, StageTimes stages,
        CancellationToken cancellationToken)
    {
        var sessionId = session.SessionId;
        if (session.AcpSessionId is { } previous)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The same ACP id on the same warm adapter, so the closed turn's late updates share it. The router opens it
            // again at the answer, in pipe order (UpdateRouter), not when this await resumes on another thread.
            var resuming = running.Link.Client.ResumeSessionAsync(new(previous, folder, mcpServers?.Invoke(sessionId) ?? []), CancellationToken.None);
            ResumeSessionResponse? resumed = null;
            try
            {
                resumed = await AnsweredAsync(adapter, running, sessionId, resuming, _ => previous, cancellationToken);
                stages.Mark("session resume");
                if (resumed.AvailableCommands.ValueKind == JsonValueKind.Array)
                    sessionStore.SetCommands(sessionId, resumed.AvailableCommands.GetRawText());
                var model = await ApplyChoicesAsync(running, sessionId, previous, resumed.ConfigOptions, stages, cancellationToken);
                return new OpenSession(adapter, running, previous, Resumed: true, session.SessionCost, model);
            }
            catch (JsonRpcException error) when (resumed is null)
            {
                // The agent lost its transcript. A new ACP session keeps the session usable; the agent won't remember it.
                Console.Error.WriteLine($"session {sessionId}: could not resume {previous} ({error.Message}); starting a new session");
            }
            catch (Exception) when (resumed is not null)
            {
                // Stopped or failed after the resume answered: the agent has the session open, so it's closed there
                // too, and anything it sends for it from now on is dropped.
                running.Updates.Forget(previous);
                await CloseQuietlyAsync(adapter, running, previous);
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var opening = running.Link.Client.NewSessionAsync(new(folder, mcpServers?.Invoke(sessionId) ?? []), CancellationToken.None);
        var acp = await AnsweredAsync(adapter, running, sessionId, opening, late => late.SessionId, cancellationToken);

        try
        {
            sessionStore.SetAcpSession(sessionId, acp.SessionId);
            stages.Mark("session new");
            if (acp.AvailableCommands.ValueKind == JsonValueKind.Array)
                sessionStore.SetCommands(sessionId, acp.AvailableCommands.GetRawText());
            var title = session.Title is null ? Title(firstText) : null;
            if (title is not null)
                sessionStore.SetTitle(sessionId, title, "prompt");
            if (acp.ConfigOptions.ValueKind == JsonValueKind.Array)
                sessionStore.SetAgentConfig(session.Agent, acp.ConfigOptions.GetRawText());
            var chosen = await ApplyChoicesAsync(running, sessionId, acp.SessionId, acp.ConfigOptions, stages, cancellationToken);
            return new OpenSession(adapter, running, acp.SessionId, Resumed: false, SavedCost: null, chosen, Titled: title is not null);
        }
        catch (Exception)
        {
            // Stopped or failed once the agent had opened it: it's closed there too, as after a resume.
            running.Updates.Forget(acp.SessionId);
            await CloseQuietlyAsync(adapter, running, acp.SessionId);
            throw;
        }
    }

    // Neither adapter honours a request's cancel, so a session/new or session/resume goes out on a token the cancel
    // doesn't cut: a cancel after it went out waits for the answer and closes the session the agent opened. That delays
    // the cancel by up to twice the close limit: the wait for the answer, then the close.
    private static async Task<T> AnsweredAsync<T>(
        SharedAdapter adapter, RunningAdapter running, string sessionId, Task<T> opening, Func<T, string> acpSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await opening.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                var late = acpSessionId(await opening.WaitAsync(SharedAdapter.CloseLimit, adapter.Time, CancellationToken.None));
                running.Updates.Forget(late);
                await CloseQuietlyAsync(adapter, running, late);
                Console.Error.WriteLine($"session {sessionId}: closed {late}, which answered after the open was cancelled");
            }
            catch (Exception error) when (error is TimeoutException or JsonRpcException or IOException or ObjectDisposedException)
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
    private async Task<string?> ApplyChoicesAsync(
        RunningAdapter running, string sessionId, string acpSessionId, JsonElement options, StageTimes stages, CancellationToken cancellationToken)
    {
        var config = sessionStore.Config(sessionId);
        var pending = config.PendingModel;
        if (options.ValueKind != JsonValueKind.Array)
        {
            if (pending is not null) throw ModelUnavailable(pending);
            return null;
        }
        var choices = config.Choices;
        var modelId = options.EnumerateArray().Where(option => option.ValueKind == JsonValueKind.Object && option.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String && category.GetString() == "model")
            .Where(option => option.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(option => option.GetProperty("id").GetString()).FirstOrDefault();
        if (pending is not null && (modelId is null || !ConfigOptions.Selects(options).Any(option => option.Id == modelId)))
            throw ModelUnavailable(pending);
        foreach (var (id, _, _) in ConfigOptions.Selects(options).ToList())
        {
            // Read again each time: setting one option can change another's values.
            var (_, current, values) = ConfigOptions.Selects(options).FirstOrDefault(option => option.Id == id);
            if (pending is not null && id == modelId)
            {
                var selected = choices.TryGetValue(id, out var chosen) && !string.IsNullOrWhiteSpace(chosen) && (values?.Contains(chosen) ?? false)
                    ? chosen : pending;
                if (!(values?.Contains(selected) ?? false)) throw ModelUnavailable(selected);
                if (selected != current)
                    options = (await running.Link.Client.SetConfigOptionAsync(acpSessionId, id, selected, cancellationToken)).ConfigOptions;
                cancellationToken.ThrowIfCancellationRequested();
                if (ConfigOptions.ModelValue(options) != selected) throw ModelUnavailable(selected);
                sessionStore.ConfirmModelSelection(sessionId, id, selected, options.GetRawText());
                pending = null;
                continue;
            }
            if (!choices.TryGetValue(id, out var value) || value == current || !(values?.Contains(value) ?? false))
                continue;
            try
            {
                options = (await running.Link.Client.SetConfigOptionAsync(acpSessionId, id, value, cancellationToken)).ConfigOptions;
            }
            catch (JsonRpcException error)
            {
                Console.Error.WriteLine($"session {sessionId}: the agent didn't take {id}={value} ({error.Message})");
            }
        }

        if (choices.Count > 0)
            stages.Mark("session config");
        sessionStore.SetConfigOptions(sessionId, options.GetRawText());
        return ConfigOptions.ModelName(options);
    }

    private static CallFailed ModelUnavailable(string selector) => new($"The selected model '{selector}' is not available for this agent.");

    /// <summary>The message's first line, cut to <see cref="TitleLength"/> characters; null when it has no text.</summary>
    public static string? Title(string text)
    {
        var line = string.Join(' ', text.Trim().Split('\n', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0)
            return null;
        return line.Length <= TitleLength ? line : line[..(TitleLength - 1)].TrimEnd() + "…";
    }
}
