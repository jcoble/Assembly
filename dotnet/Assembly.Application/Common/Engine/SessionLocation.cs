namespace Assembly.Application.Common.Engine;

/// <summary>The owning session's authoritative folder and persisted checkout policy.</summary>
public sealed record SessionLocation(string Folder, bool ManagedCheckout);
