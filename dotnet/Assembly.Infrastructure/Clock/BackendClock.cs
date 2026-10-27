using System.Text.Json;
using Assembly.Application.Common;
using Assembly.Application.Common.Calls;
using Assembly.Application.Features.Chat.Sessions;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Infrastructure.Clock;

/// <summary>
/// The one backend clock (plan 5.8): a <see cref="PeriodicTimer"/> on the injected <see cref="TimeProvider"/> that
/// publishes <c>{ now }</c> on the <c>clock</c> topic, key <c>second</c>, while someone is subscribed, and tells the
/// machines probe each tick. It runs while <c>clock</c> or <c>machines</c> has a subscriber: the first subscription
/// starts it, and the first tick that finds neither stops it.
/// </summary>
internal sealed class BackendClock : IFeature
{
    private const string Topic = "clock";
    private const string Second = "second";

    private readonly TopicSet _topics;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly BackgroundWork _work;
    private bool _running;
    // Set by a subscribe, so a subscription that lands while a tick looks for one keeps the timer going.
    private bool _asked;

    /// <summary>Each tick, with whether <c>machines</c> has a subscriber; false on the tick that stops the clock too.</summary>
    internal event Action<bool>? Ticked;

    public BackendClock(TopicSet topics, TimeProvider time, BackgroundWork work)
    {
        _topics = topics;
        _time = time;
        _work = work;
    }

    public void Register(ICallRegistry calls) =>
        calls.Topic(Topic, ClockJson.Default.ClockTime, _ => Second, (key, _) =>
        {
            if (key is not (null or Second))
                throw new CallFailed($"the clock has no {key}; it has {Second}");
            Start();
            return [Now()];
        });

    // Runs inside the topics' lock, with the subscription it's for (clock's or machines'), so a tick's HasSubscribers
    // sees it or waits for it.
    internal void Start()
    {
        lock (_gate)
        {
            _asked = true;
            if (_running)
                return;
            _running = true;
        }
        _work.Start("clock", TickAsync);
    }

    private async Task TickAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping))
            {
                lock (_gate)
                    _asked = false;
                var watched = _topics.HasSubscribers(Topic);
                var machinesWatched = _topics.HasSubscribers(MachinesFeature.Topic);
                bool idle;
                lock (_gate)
                {
                    idle = !watched && !machinesWatched && !_asked;
                    if (idle)
                        _running = false;
                }
                if (idle)
                {
                    Ticked?.Invoke(false);
                    return;
                }
                if (watched)
                    _topics.Publish(Topic, Second, JsonSerializer.Serialize(Now(), ClockJson.Default.ClockTime));
                Ticked?.Invoke(machinesWatched);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private ClockTime Now() => new(_time.GetUtcNow().ToUnixTimeMilliseconds());
}
