using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>Captures one actual source for the existing rows reads and subscription.</summary>
public interface IRowSourceBindings
{
    /// <summary>Rows reads: a local session, or a remote session's saved copy here.</summary>
    Task<RowSourceBinding> CaptureAsync(ExpectedSource source, string session, CancellationToken ct);

    /// <summary>A call that acts on the session's machine, so a remote session needs its live owner.</summary>
    Task<RowSourceBinding> CaptureLiveAsync(ExpectedSource source, string session, CancellationToken ct) => CaptureAsync(source, session, ct);

    Task<IExpectedSourceGuard> CaptureEngineAsync(ExpectedSource source, CancellationToken ct) =>
        throw new CallFailed("Source-bound Engine reads are unavailable.", "source_changed");
}

public abstract class RowSourceBinding : IExpectedSourceGuard
{
    public abstract CancellationToken Changed { get; }
    public abstract void EnsureCurrent();
    // A display-cache write needs its own identity certification on the caller's transaction.
    // A read binding alone does not certify a copied facade.
    public virtual void EnsureDisplayCurrent(IReadScope? scope = null) =>
        throw new CallFailed("The selected display source cannot be certified.", "source_changed");
    public abstract void Watch();
    public abstract string? ActiveRun { get; }
    public abstract void Dispose();
}
