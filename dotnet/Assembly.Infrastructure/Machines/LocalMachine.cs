using Assembly.Application.Features.Chat.Attachments;
using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

using Assembly.Application.Common.Machines;
namespace Assembly.Infrastructure.Machines;

/// <summary>Threads whose agent runs here: their sessions, rows and attachments are all on this machine.</summary>
public sealed class LocalMachine(LiveHub hub, ThreadStore threads, SessionRegistry sessions, AttachmentStore attachments) : IMachine
{
    public string? Name => null;

    public Task<IReadOnlyList<(string Id, string Name)>> AgentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(string Id, string Name)>>(sessions.Agents.Select(agent => (agent.Id, agent.Name)).ToList());

    public Task<ThreadInfo> CreateThreadAsync(string agent, string repoUrl, CancellationToken cancellationToken) =>
        Task.FromResult(threads.Create(agent, repoUrl));

    public Task<ThreadInfo> CreateHelperAsync(ThreadInfo parent, string agent, string title, CancellationToken cancellationToken) =>
        Task.FromResult(threads.Helper(parent, agent, title));

    public Task<string?> SendAsync(ThreadInfo thread, string text, CancellationToken cancellationToken) =>
        sessions.For(thread.ThreadId).SteerAsync(text, cancellationToken);

    public Task<bool> StopAsync(ThreadInfo thread, string runId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.Stop(runId));

    // A helper session's row in its parent's thread stops by the helper's thread id.
    public Task<bool> StopTaskAsync(ThreadInfo thread, string taskId, CancellationToken cancellationToken) =>
        threads.Get(taskId) is { Helper: true } helper && helper.ParentThreadId == thread.ThreadId
            ? Task.FromResult(sessions.ActiveRun(taskId) is { } run && sessions.Stop(run))
            : sessions.For(thread.ThreadId).StopTaskAsync(taskId, cancellationToken);

    public string? ActiveRun(ThreadInfo thread) => sessions.ActiveRun(thread.ThreadId);

    public Task<StoredToolCall?> ToolCallAsync(ThreadInfo thread, string itemId, CancellationToken cancellationToken) =>
        Task.FromResult(hub.ToolCall(thread.ThreadId, itemId));

    public Task<string> SaveAttachmentAsync(ThreadInfo thread, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        attachments.SaveAsync(thread.ThreadId, type, bytes, cancellationToken);

    public Task<ThreadConfig> ConfigAsync(ThreadInfo thread, CancellationToken cancellationToken)
    {
        var config = threads.Config(thread.ThreadId);
        return Task.FromResult(config.Options is null ? config with { Options = threads.AgentConfig(thread.Agent) } : config);
    }

    public async Task<ThreadConfig> SetConfigAsync(ThreadInfo thread, string configId, string value, CancellationToken cancellationToken)
    {
        await sessions.For(thread.ThreadId).SetConfigAsync(configId, value, cancellationToken);
        return await ConfigAsync(thread, cancellationToken);
    }

    public Task<bool> AnswerAsync(ThreadInfo thread, string askId, System.Text.Json.JsonElement answer, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.For(thread.ThreadId).Answer(askId, answer));

    public void Watch(ThreadInfo thread)
    {
    }
}
