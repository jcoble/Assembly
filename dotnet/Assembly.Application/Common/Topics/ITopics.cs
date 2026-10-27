namespace Assembly.Application.Common.Topics;

/// <summary>
/// Where the writer sends a write's publishes, after it commits and before it lets go of its lock, so they go out in
/// commit order. Publishing only queues the change for each subscriber; it never waits on a send.
/// </summary>
public interface ITopics
{
    void Publish(string topic, string? key, string json);
}
