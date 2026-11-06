namespace Assembly.Application.Common.Calls;

/// <summary>Server-owned read lifetime; checked again before a prepared frame leaves its socket queue.</summary>
public interface IExpectedSourceGuard : IDisposable
{
    CancellationToken Changed { get; }
    void EnsureCurrent();
}
