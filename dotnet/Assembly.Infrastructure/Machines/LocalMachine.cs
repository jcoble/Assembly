using Assembly.Application.Features.Chat.Attachments;
using Assembly.Application.Common.Acp.JsonRpc;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat;
using Assembly.Domain.Sessions;

using Assembly.Application.Common.Machines;
namespace Assembly.Infrastructure.Machines;

/// <summary>Sessions whose agent runs here: their ACP sessions, rows and attachments are all on this machine.</summary>
public sealed class LocalMachine(LiveHub hub, SessionStore sessionStore, SessionRegistry sessions, AttachmentStore attachments) : IMachine
{
    public string? Name => null;

    public Task<IReadOnlyList<(string Id, string Name)>> AgentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(string Id, string Name)>>(sessions.Agents.Select(agent => (agent.Id, agent.Name)).ToList());

    public Task<SessionInfo> CreateSessionAsync(string agent, string repoUrl, CancellationToken cancellationToken) =>
        Task.FromResult(sessionStore.Create(agent, repoUrl));

    public Task<SessionInfo> CreateHelperAsync(SessionInfo parent, string agent, string title, CancellationToken cancellationToken) =>
        Task.FromResult(sessionStore.Helper(parent, agent, title));

    public async Task<string?> SendAsync(SessionInfo session, string text, string? clientId, CancellationToken cancellationToken)
    {
        var host = sessions.For(session.SessionId);
        var label = Assembly.Application.Features.Chat.Helpers.MessageFrame.SenderOf(text);
        var sent = await host.SendAsync(text, clientId, label, cancellationToken);
        if (sent is SendResult.Busy)
        {
            var turn = host.CurrentTurn;
            SteerResult outcome;
            try { outcome = await host.SteerAsync(text, clientId, label, cancellationToken); }
            catch (Exception error) when (error is JsonRpcException or IOException or ObjectDisposedException) { return null; }
            if (outcome == SteerResult.Injected) return turn.RunId;
            if (outcome == SteerResult.PromptRequired && turn.RunId is not null)
                await turn.Done.WaitAsync(cancellationToken);
            else if (outcome != SteerResult.NotRunning) return null;
            sent = await host.SendAsync(text, clientId, label, cancellationToken);
        }
        return sent switch
        {
            SendResult.Inserted inserted => inserted.RunId,
            SendResult.Unknown => host.LegacyRunOfClient(clientId, text, label),
            SendResult.AuthRequired => throw new Assembly.Application.Common.Calls.CallFailed("authentication required"),
            SendResult.Failed failed => throw new Assembly.Application.Common.Calls.CallFailed(failed.Reason),
            _ => null
        };
    }

    public Task<bool> StopAsync(SessionInfo session, string runId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.Stop(runId));

    public Task<bool> StopTaskAsync(SessionInfo session, string taskId, CancellationToken cancellationToken) =>
        sessions.For(session.SessionId).StopTaskAsync(taskId, cancellationToken);

    public string? ActiveRun(SessionInfo session) => sessions.ActiveRun(session.SessionId);

    public Task<StoredToolCall?> ToolCallAsync(SessionInfo session, string itemId, CancellationToken cancellationToken) =>
        Task.FromResult(hub.ToolCall(session.SessionId, itemId));

    public Task<string> SaveAttachmentAsync(SessionInfo session, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        attachments.SaveAsync(session.SessionId, type, bytes, cancellationToken);

    public Task<SessionConfig> ConfigAsync(SessionInfo session, CancellationToken cancellationToken)
    {
        var config = sessionStore.Config(session.SessionId);
        return Task.FromResult(config.Options is null ? config with { Options = sessionStore.AgentConfig(session.Agent) } : config);
    }

    public async Task<SessionConfig> SetConfigAsync(SessionInfo session, string configId, string value, CancellationToken cancellationToken)
    {
        await sessions.For(session.SessionId).SetConfigAsync(configId, value, cancellationToken);
        return await ConfigAsync(session, cancellationToken);
    }

    public Task<bool> AnswerAsync(SessionInfo session, string askId, System.Text.Json.JsonElement answer, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.For(session.SessionId).Answer(askId, answer));

    public void Watch(SessionInfo session)
    {
    }
}
