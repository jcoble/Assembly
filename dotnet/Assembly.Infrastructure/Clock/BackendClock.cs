using Assembly.Application.Common.Calls;
using TopicSet = Assembly.Infrastructure.Topics.Topics;

namespace Assembly.Infrastructure.Clock;

/// <summary>
/// The one backend clock (plan 5.8): a <see cref="PeriodicTimer"/> on the injected <see cref="TimeProvider"/> that
/// publishes <c>{ now }</c> on the <c>clock</c> topic, key <c>second</c>, while someone is subscribed.
/// </summary>
internal sealed class BackendClock : IFeature, IDisposable
{
    private readonly TopicSet _topics;
    private readonly TimeProvider _time;

    public BackendClock(TopicSet topics, TimeProvider time)
    {
        _topics = topics;
        _time = time;
    }

    public void Register(ICallRegistry calls) => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}
