using System.Text.Json;
using Assembly.Application.Common.Calls;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Infrastructure.Clock;

/// <summary>
/// The one backend clock (plan 5.8): a <see cref="PeriodicTimer"/> on the injected <see cref="TimeProvider"/> that
/// publishes <c>{ now }</c> on the <c>clock</c> topic, key <c>second</c>, while someone is subscribed. The first
/// subscription starts it, and the first tick that finds no subscriber stops it.
/// </summary>
internal sealed class BackendClock : IFeature, IDisposable
{
    private const string Topic = "clock";
    private const string Second = "second";

    private readonly TopicSet _topics;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopped = new();
    private bool _running;
    // Set by a subscribe, so a subscription that lands while a tick looks for one keeps the timer going.
    private bool _asked;

    public BackendClock(TopicSet topics, TimeProvider time)
    {
        _topics = topics;
        _time = time;
    }

    public void Register(ICallRegistry calls) =>
        calls.Topic(Topic, ClockJson.Default.ClockTime, _ => Second, (key, _) =>
        {
            if (key is not (null or Second))
                throw new CallFailed($"the clock has no {key}; it has {Second}");
            Start();
            return [Now()];
        });

    // Runs inside the topics' lock, with the subscription it's for, so a tick's HasSubscribers sees it or waits for it.
    private void Start()
    {
        lock (_gate)
        {
            _asked = true;
            if (_running)
                return;
            _running = true;
        }
        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopped.Token))
            {
                lock (_gate)
                    _asked = false;
                var watched = _topics.HasSubscribers(Topic);
                lock (_gate)
                {
                    if (!watched && !_asked)
                    {
                        _running = false;
                        return;
                    }
                }
                _topics.Publish(Topic, Second, JsonSerializer.Serialize(Now(), ClockJson.Default.ClockTime));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private ClockTime Now() => new(_time.GetUtcNow().ToUnixTimeMilliseconds());

    // Cancelled, not disposed, so a late subscribe or a tick still reading the token doesn't throw.
    public void Dispose() => _stopped.Cancel();
}
