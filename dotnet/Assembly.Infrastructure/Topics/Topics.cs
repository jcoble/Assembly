using Assembly.Application.Common.Topics;

namespace Assembly.Infrastructure.Topics;

/// <summary>The host's one set of topics. Nobody subscribes yet (step B5 adds subscriptions), so a change is dropped.</summary>
public sealed class Topics : ITopics
{
    public void Publish(string topic, string? key, string json)
    {
    }
}
