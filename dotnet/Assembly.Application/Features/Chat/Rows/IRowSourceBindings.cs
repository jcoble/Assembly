using System.Text.Json;
using Assembly.Application.Common.Calls;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>Captures one actual source for the existing rows reads and subscription.</summary>
public interface IRowSourceBindings
{
    Task<RowSourceBinding> CaptureAsync(ExpectedSource source, string session, CancellationToken ct);
}

public abstract class RowSourceBinding : IExpectedSourceGuard
{
    public abstract bool Remote { get; }
    public abstract CancellationToken Changed { get; }
    public abstract void EnsureCurrent();
    public abstract void Watch();
    public abstract string? ActiveRun { get; }
    public abstract Task<JsonElement> PageAsync(long before, int limit, CancellationToken ct);
    public abstract Task<SubagentPage> SubagentsAsync(string? after, int limit, CancellationToken ct);
    public abstract IAsyncEnumerable<JsonElement> WatchAsync(long? after, CancellationToken ct);
    public abstract void Dispose();
}
