using Assembly.Application.Common.Topics;

namespace Assembly.Tests.Draft.Terminal;

/// <summary>Every publish, in order, and a wait for the one a test expects.</summary>
internal sealed class RecordingTopics : ITopics
{
    private readonly Lock _lock = new();
    private readonly List<(Func<(string Topic, string? Key, string Json), bool> Match, TaskCompletionSource Done)> _waits = [];

    public List<(string Topic, string? Key, string Json)> Published { get; } = [];

    public void Publish(string topic, string? key, string json, bool keyedOnly = false)
    {
        lock (_lock)
        {
            Published.Add((topic, key, json));
            foreach (var wait in _waits.Where(wait => wait.Match((topic, key, json))).ToList())
            {
                _waits.Remove(wait);
                wait.Done.TrySetResult();
            }
        }
    }

    public bool HasSubscribers(string topic, string key) => true;

    public List<(string Topic, string? Key, string Json)> Snapshot()
    {
        lock (_lock)
            return [.. Published];
    }

    public Task WaitFor(Func<(string Topic, string? Key, string Json), bool> match)
    {
        lock (_lock)
        {
            if (Published.Any(match))
                return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waits.Add((match, done));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
