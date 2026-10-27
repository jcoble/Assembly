using System.Text.Json;
using Assembly.Application.Common.Machines;
using Assembly.Application.Features.Chat;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Tests.Draft.Base.Chat;

/// <summary>
/// A machine for the chat socket tests: it records what it's asked, can be down, and can hold a call until it's
/// released or cancelled, to show a cancel reaches it.
/// </summary>
internal sealed class FakeMachine(string? name, SessionStore store) : IMachine
{
    private TaskCompletionSource? _held;

    public string? Name => name;
    public bool Down { get; set; }

    /// <summary>Turns down a send, stop or answer, as a machine does when the turn or ask can't take it.</summary>
    public bool Refuses { get; set; }
    public List<string> Asked { get; } = [];
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set when a held call reaches the machine.</summary>
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The run <see cref="ActiveRun"/> answers.</summary>
    public string? Running { get; set; }
    public StoredToolCall? Tool { get; set; }
    public SessionConfig Config { get; set; } = new("[]", new Dictionary<string, string>());

    /// <summary>From now on, each call waits until <see cref="Release"/> or its token fires.</summary>
    public void Hold() => _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _held?.TrySetResult();

    public Task<IReadOnlyList<(string Id, string Name)>> AgentsAsync(CancellationToken cancellationToken) =>
        Remote<IReadOnlyList<(string, string)>>("agents", () => [("claude", "Claude")], cancellationToken);

    public Task<SessionInfo> CreateSessionAsync(string agent, string repoUrl, CancellationToken cancellationToken) =>
        Remote("create", () => name is null ? store.Create(agent, repoUrl) : store.Add(Guid.NewGuid().ToString(), agent, repoUrl, name),
            cancellationToken);

    public Task<SessionInfo> CreateHelperAsync(SessionInfo parent, string agent, string title, CancellationToken cancellationToken) =>
        Remote("helper", () => store.Helper(parent, agent, title), cancellationToken);

    public Task<string?> SendAsync(SessionInfo session, string text, CancellationToken cancellationToken) =>
        Remote<string?>($"send {session.SessionId} {text}", () => Refuses ? null : "run-1", cancellationToken);

    public Task<bool> StopAsync(SessionInfo session, string runId, CancellationToken cancellationToken) =>
        Remote($"stop {runId}", () => !Refuses, cancellationToken);

    public Task<bool> StopTaskAsync(SessionInfo session, string taskId, CancellationToken cancellationToken) =>
        Remote($"stop task {taskId}", () => !Refuses, cancellationToken);

    public string? ActiveRun(SessionInfo session) => Running;

    public Task<StoredToolCall?> ToolCallAsync(SessionInfo session, string itemId, CancellationToken cancellationToken) =>
        Remote($"tool {itemId}", () => Tool, cancellationToken);

    public Task<string> SaveAttachmentAsync(SessionInfo session, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<SessionConfig> ConfigAsync(SessionInfo session, CancellationToken cancellationToken) =>
        Remote("config", () => Config, cancellationToken);

    public Task<SessionConfig> SetConfigAsync(SessionInfo session, string configId, string value, CancellationToken cancellationToken) =>
        Remote($"set config {configId} {value}", () => Config, cancellationToken);

    public Task<bool> AnswerAsync(SessionInfo session, string askId, JsonElement answer, CancellationToken cancellationToken) =>
        Remote($"answer {askId} {answer.GetRawText()}", () => !Refuses, cancellationToken);

    public void Watch(SessionInfo session) => Asked.Add($"watch {session.SessionId}");

    private async Task<T> Remote<T>(string asked, Func<T> answer, CancellationToken cancellationToken)
    {
        lock (Asked)
            Asked.Add(asked);
        if (Down)
            throw new MachineUnreachable($"{name} is down");
        if (_held is { } held)
        {
            Reached.TrySetResult();
            try
            {
                await held.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
        return answer();
    }
}
