namespace Assembly.Application.Common.Topics;

/// <summary>
/// Where the writer sends a write's publishes, after it commits and before it lets go of its lock, so they go out in
/// commit order. Publishing only queues the change for each subscriber; it never waits on a send.
/// </summary>
public interface ITopics
{
    /// <summary>
    /// Sends the change to the topic's subscribers for its key and, unless <paramref name="keyedOnly"/>, to those with
    /// no key: a change to an item the topic's keyless state leaves out goes only to its own subscribers.
    /// </summary>
    void Publish(string topic, string? key, string json, bool keyedOnly = false);

    /// <summary>Whether a publish on the topic under this key would reach anyone, so a publisher can skip building it.</summary>
    bool HasSubscribers(string topic, string key);
}
