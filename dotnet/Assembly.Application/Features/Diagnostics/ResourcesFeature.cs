using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Topics;

namespace Assembly.Application.Features.Diagnostics;

/// <summary>Stub: the resources topic, still to come.</summary>
internal sealed class ResourcesFeature : IFeature
{
    public const string Topic = "resources";

    public ResourcesFeature(ITopics topics, Action startClock, Func<IReadOnlyList<ProcessReading>> read, TimeProvider time)
    {
    }

    public void Register(ICallRegistry calls) =>
        calls.Topic(Topic, ResourcesJson.Default.ResourceUsage, _ => "backend", (_, _) => []);

    public void Tick(bool watched)
    {
    }
}
