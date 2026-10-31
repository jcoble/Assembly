using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Engine.Persistence;

public interface IEngineRetention
{
    long? NextDeadlineMs(IReadScope s, long nowMs);
    void Apply(IWriteScope s, long nowMs);
}

public sealed class NoRetention : IEngineRetention
{
    public long? NextDeadlineMs(IReadScope s, long nowMs) => null;
    public void Apply(IWriteScope s, long nowMs) { }
}
