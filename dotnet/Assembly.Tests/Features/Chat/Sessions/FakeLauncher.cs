using System.Threading.Channels;
using Assembly.Application.Common.Agents;
using Assembly.Infrastructure.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

namespace Assembly.Tests.Features.Chat.Sessions;

/// <summary>Checks every thread out at <c>/work/thread</c>, and hands out a new <see cref="FakeLink"/> per launch, or runs <see cref="Launch"/> in its place.</summary>
internal sealed class FakeLauncher : IAgentLauncher
{
    public const string Checkout = "/work/thread";

    private readonly Channel<FakeLink> _links = Channel.CreateUnbounded<FakeLink>();
    private int _launches;

    public int Launches => _launches;

    public Func<CancellationToken, Task<IAgentLink>>? Launch { get; set; }

    public IReadOnlyList<AgentDescriptor> Agents { get; } =
    [
        new("claude", "Claude Code", "claude-agent-acp", [], null),
        new("codex", "Codex", "codex-acp", [], null),
    ];

    /// <summary>The agent of each launch, in order.</summary>
    public List<string> Launched { get; } = [];

    public Task<string> CheckoutAsync(ThreadInfo thread, StageTimes stages, CancellationToken cancellationToken) => Task.FromResult(Checkout);

    public Task<IAgentLink> LaunchAsync(string agentId, StageTimes stages, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _launches);
        lock (Launched)
            Launched.Add(agentId);
        if (Launch is { } launch)
            return launch(cancellationToken);
        var link = new FakeLink();
        _links.Writer.TryWrite(link);
        return Task.FromResult<IAgentLink>(link);
    }

    public async Task<FakeLink> NextAsync() => await _links.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}
