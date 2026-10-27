using Assembly.Application.Common.Topics;

namespace Assembly.Infrastructure.Topics;

/// <summary>
/// The host's one set of topics and who subscribes to each (plan 5.7). A subscribe reads the state and registers under
/// the lock <see cref="Publish"/> takes too, so a change is neither lost nor sent before its state. The writer publishes
/// while it holds its own lock, so the order is always writer, then Topics. Delivering only queues a frame on the
/// subscriber's socket; it never waits on a send.
/// </summary>
public sealed class Topics : ITopics
{
    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscriptions = [];

    public void Publish(string topic, string? key, string json)
    {
        lock (_lock)
            foreach (var subscription in _subscriptions)
                if (subscription.Topic == topic && (subscription.Key is null || subscription.Key == key))
                    subscription.Change(key, json);
    }

    public bool HasSubscribers(string topic)
    {
        lock (_lock)
            return _subscriptions.Exists(subscription => subscription.Topic == topic);
    }

    /// <summary>
    /// Runs <paramref name="state"/>, which reads the state and queues it, then registers the subscription, both under
    /// the lock. With no key, it gets every change on the topic; with a key, only changes published with that key.
    /// </summary>
    internal Subscription Subscribe(string topic, string? key, Action state, Action<string?, string> change)
    {
        lock (_lock)
        {
            state();
            var subscription = new Subscription(topic, key, change);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    internal void Unsubscribe(Subscription subscription)
    {
        lock (_lock)
            _subscriptions.Remove(subscription);
    }

    internal sealed class Subscription(string topic, string? key, Action<string?, string> change)
    {
        public string Topic { get; } = topic;
        public string? Key { get; } = key;
        public Action<string?, string> Change { get; } = change;
    }
}
