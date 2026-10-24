namespace Assembly.Engine.Live;

/// <summary>Ends a subscription whose socket stopped reading. The webview reopens from its watermark and misses nothing.</summary>
public sealed class LiveFellBehind() : Exception("The socket fell too far behind and was closed.");
