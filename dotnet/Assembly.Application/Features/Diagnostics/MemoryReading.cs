namespace Assembly.Application.Features.Diagnostics;

/// <summary>
/// The RSS in KB of the app, its WebContent process and this backend. Off the Mac there's no app, so its fields are null.
/// </summary>
public sealed record MemoryReading(int? AppPid, long? AppRssKb, int? WebContentPid, long? WebContentRssKb, int BackendPid, long? BackendRssKb);
