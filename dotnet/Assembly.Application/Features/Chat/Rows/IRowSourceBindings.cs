using System.Text.Json;
using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>Captures one actual source for the existing rows reads and subscription.</summary>
public interface IRowSourceBindings
{
    Task<RowSourceBinding> CaptureAsync(ExpectedSource source, string session, CancellationToken ct);
    Task<IExpectedSourceGuard> CaptureEngineAsync(ExpectedSource source, CancellationToken ct) =>
        throw new CallFailed("Source-bound Engine reads are unavailable.", "source_changed");
}

public abstract class RowSourceBinding : IExpectedSourceGuard
{
    public abstract bool Remote { get; }
    public abstract CancellationToken Changed { get; }
    public abstract void EnsureCurrent();
    // A display-cache write needs its own identity certification on the caller's transaction.
    // A read binding alone does not certify a copied facade.
    public virtual void EnsureDisplayCurrent(IReadScope? scope = null) =>
        throw new CallFailed("The selected display source cannot be certified.", "source_changed");
    public abstract void Watch();
    public abstract string? ActiveRun { get; }
    public abstract Task<JsonElement> PageAsync(long before, int limit, CancellationToken ct);
    public abstract Task<SubagentPage> SubagentsAsync(string? after, int limit, CancellationToken ct);
    public abstract IAsyncEnumerable<JsonElement> WatchAsync(long? after, CancellationToken ct);
    public virtual Task<IReadOnlyList<FileTouched>> FilesAsync(CancellationToken ct) =>
        throw new CallFailed("Source-bound file context is unavailable.", "source_changed");
    public virtual Task<SessionAttachments> AttachmentsAsync(CancellationToken ct) =>
        throw new CallFailed("Source-bound attachment context is unavailable.", "source_changed");
    public abstract void Dispose();
}
