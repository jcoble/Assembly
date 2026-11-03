using Assembly.Infrastructure.Chat.Attachments;
using Assembly.Application.Common.Agents;
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

    /// <summary>Turns down a stop or answer, as a machine does when the turn or ask can't take it.</summary>
    public bool Refuses { get; set; }
    public List<string> Asked { get; } = [];
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set when a held call reaches the machine.</summary>
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The run <see cref="ActiveRun"/> answers.</summary>
    public string? Running { get; set; }
    public StoredToolCall? Tool { get; set; }
    public AttachmentStore? Attachments { get; set; }
    /// <summary>The last image saved here.</summary>
    public byte[]? Saved { get; private set; }

    public SessionConfig Config { get; set; } = new("[]", new Dictionary<string, string>());

    /// <summary>From now on, each call waits until <see cref="Release"/> or its token fires.</summary>
    public void Hold() => _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _held?.TrySetResult();

    /// <summary>
    /// A held call has already done its work: a cancel only sets <see cref="Cancelled"/>, and the call still answers once
    /// released.
    /// </summary>
    public bool Started { get; set; }

    /// <summary>How many probes reached it. Each answers as <see cref="Probe"/> does, which may ignore the probe's token.</summary>
    public int Probes;
    public Func<CancellationToken, Task> Probe { get; set; } = _ => Task.CompletedTask;

    public Task ProbeAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Probes);
        return Probe(cancellationToken);
    }

    public Task<IReadOnlyList<ConfigTarget>> AgentsAsync(CancellationToken cancellationToken) =>
        Remote<IReadOnlyList<ConfigTarget>>("agents", () => [new("claude", "Claude", new string('a', 64), "", "claude/")], cancellationToken);

    public Task<SessionInfo> CreateSessionAsync(string agent, string repoUrl, CancellationToken cancellationToken,
        InitialSessionConfig? initialConfig = null, string? workingFolder = null) =>
        Remote("create", () => name is null ? store.Create(agent, repoUrl, initialConfig, workingFolder)
            : store.Add(Guid.NewGuid().ToString(), agent, repoUrl, name, workingFolder: workingFolder), cancellationToken);

    public Task<SessionInfo> CreateHelperAsync(SessionInfo parent, string agent, string title, CancellationToken cancellationToken) =>
        Remote("helper", () => store.Helper(parent, agent, title), cancellationToken);

    public Task<bool> StopAsync(SessionInfo session, string runId, CancellationToken cancellationToken) =>
        Remote($"stop {runId}", () => !Refuses, cancellationToken);

    public Task<bool> StopTaskAsync(SessionInfo session, string taskId, CancellationToken cancellationToken) =>
        Remote($"stop task {taskId}", () => !Refuses, cancellationToken);

    public string? ActiveRun(SessionInfo session) => Running;

    public Task<StoredToolCall?> ToolCallAsync(SessionInfo session, string itemId, CancellationToken cancellationToken) =>
        Remote($"tool {itemId}", () => Tool, cancellationToken);

    public Task<string> SaveAttachmentAsync(SessionInfo session, ImageType type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        Remote($"save {type.Extension} {bytes.Length}", () =>
        {
            Saved = bytes.ToArray();
            return $"/{name ?? "here"}/image.{type.Extension}";
        }, cancellationToken);

    public async Task<AttachmentPiece> ReadAttachmentAsync(SessionInfo session, string name, long offset, CancellationToken cancellationToken)
    {
        await Remote($"read {session.SessionId} {name} {offset}", () => 0, cancellationToken);
        return await (Attachments ?? throw new InvalidOperationException("The test machine's attachment store was not configured."))
            .ReadAsync(session.SessionId, name, offset, cancellationToken);
    }

    public Task<SessionConfig> ConfigAsync(SessionInfo session, CancellationToken cancellationToken) =>
        Remote("config", () => Config, cancellationToken);

    public Task<SessionConfig> SetConfigAsync(SessionInfo session, string configId, string value, CancellationToken cancellationToken, string? targetId = null) =>
        Remote($"set config {configId} {value}" + (targetId is null ? "" : $" target {targetId}"), () => Config, cancellationToken);

    public Task<ComposerCatalogue> DiscoverConfigAsync(string agent, string targetId, string cwd, CancellationToken cancellationToken) =>
        Remote($"discover {agent} {targetId} {cwd}", () => new ComposerCatalogue(Config, new Dictionary<string, string>()), cancellationToken);

    public Task<SessionConfig> WarmConfigAsync(SessionInfo session, string targetId, CancellationToken cancellationToken) =>
        Remote($"warm config {targetId}", () => Config, cancellationToken);

    public Task<bool> AnswerAsync(SessionInfo session, string askId, JsonElement answer, CancellationToken cancellationToken) =>
        Remote($"answer {askId} {answer.GetRawText()}", () => !Refuses, cancellationToken);

    public void Watch(SessionInfo session) => Asked.Add($"watch {session.SessionId}");

    private async Task<T> Remote<T>(string asked, Func<T> answer, CancellationToken cancellationToken)
    {
        lock (Asked)
            Asked.Add(asked);
        if (Down)
            throw new MachineUnreachable(name ?? "here", $"{name} is down");
        if (_held is { } held)
        {
            Reached.TrySetResult();
            if (Started)
            {
                using (cancellationToken.Register(() => Cancelled.TrySetResult()))
                    await held.Task;
                return answer();
            }
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
