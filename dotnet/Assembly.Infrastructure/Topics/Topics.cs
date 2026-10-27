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

    public void Publish(string topic, string? key, string json, bool keyedOnly = false)
    {
        lock (_lock)
            foreach (var subscription in _subscriptions)
                if (subscription.Topic == topic && (subscription.Key is null ? !keyedOnly : subscription.Key == key))
                    subscription.Change(key, json);
    }

    public bool HasSubscribers(string topic, string key)
    {
        lock (_lock)
            return _subscriptions.Exists(subscription => subscription.Topic == topic && (subscription.Key is null || subscription.Key == key));
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
    internal Subscription Subscribe(string topic, string? key, Action<Subscription> state, Action<string?, string> change)
    {
        lock (_lock)
        {
            var subscription = new Subscription(topic, key, change);
            state(subscription);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    public event Action<string, string?>? Unsubscribed;

    internal void Unsubscribe(Subscription subscription)
    {
        bool last;
        lock (_lock)
        {
            subscription.Open = false;
            last = _subscriptions.Remove(subscription)
                && !_subscriptions.Exists(other => other.Topic == subscription.Topic && other.Key == subscription.Key);
        }
        if (last)
            Unsubscribed?.Invoke(subscription.Topic, subscription.Key);
    }

    internal sealed class Subscription(string topic, string? key, Action<string?, string> change)
    {
        private volatile bool _open = true;

        public string Topic { get; } = topic;
        public string? Key { get; } = key;
        public Action<string?, string> Change { get; } = change;

        /// <summary>Until it's unsubscribed; its frames still queued then are dropped.</summary>
        public bool Open
        {
            get => _open;
            set => _open = value;
        }
    }
}
