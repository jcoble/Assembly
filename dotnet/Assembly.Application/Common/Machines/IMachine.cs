using Assembly.Application.Features.Chat.Attachments;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Common.Machines;

/// <summary>
/// Where a session's agent runs: this machine, or a remote one this computer copies the rows from. The routes ask the
/// session's machine to create, send and stop, and for anything a remote session hasn't copied yet.
/// </summary>
public interface IMachine
{
    /// <summary>The name a session records for this machine; null for the machine the routes run on.</summary>
    string? Name { get; }

    /// <summary>The agents a new session on this machine can run, as (id, name).</summary>
    Task<IReadOnlyList<(string Id, string Name)>> AgentsAsync(CancellationToken cancellationToken);

    /// <summary>Asks whether the machine answers, recording what it finds; this machine always does.</summary>
    Task ProbeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task<SessionInfo> CreateSessionAsync(string agent, string repoUrl, CancellationToken cancellationToken);

    /// <summary>A helper session under <paramref name="parent"/>, which may be on another machine, in its repo.</summary>
    Task<SessionInfo> CreateHelperAsync(SessionInfo parent, string agent, string title, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a turn, or sends the text into the one running, and returns the run id; null if the running turn
    /// can't take it. A resend with the same <paramref name="clientId"/> answers the first send's run id.
    /// </summary>
    Task<string?> SendAsync(SessionInfo session, string text, string? clientId, CancellationToken cancellationToken);

    /// <summary>Stops the run. False if the session isn't running it.</summary>
    Task<bool> StopAsync(SessionInfo session, string runId, CancellationToken cancellationToken);

    /// <summary>Stops one of the session's background tasks, or a subagent by its child session id. False if it isn't running, can't be stopped, or the agent said no.</summary>
    Task<bool> StopTaskAsync(SessionInfo session, string taskId, CancellationToken cancellationToken);

    /// <summary>The session's running turn, or null when it's idle.</summary>
    string? ActiveRun(SessionInfo session);

    Task<StoredToolCall?> ToolCallAsync(SessionInfo session, string itemId, CancellationToken cancellationToken);

    /// <summary>Stores the image where the agent can read it and returns its path there.</summary>
    Task<string> SaveAttachmentAsync(SessionInfo session, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>
    /// The session's config: the options its agent reported (before its first session, the ones the agent's
    /// latest new session had, if any), and the values chosen for it.
    /// </summary>
    Task<SessionConfig> ConfigAsync(SessionInfo session, CancellationToken cancellationToken);

    /// <summary>Chooses a value for one of the session's config options, and returns its config after.</summary>
    Task<SessionConfig> SetConfigAsync(SessionInfo session, string configId, string value, CancellationToken cancellationToken);

    /// <summary>Answers one of the session's open asks (its card row). False if it isn't open or the answer doesn't fit.</summary>
    Task<bool> AnswerAsync(SessionInfo session, string askId, System.Text.Json.JsonElement answer, CancellationToken cancellationToken);

    /// <summary>A webview opened the session: a remote machine brings its rows up to date here. Nothing to do locally.</summary>
    void Watch(SessionInfo session);
}
