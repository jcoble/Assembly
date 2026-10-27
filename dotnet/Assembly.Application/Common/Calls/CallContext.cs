namespace Assembly.Application.Common.Calls;

/// <summary>One call's token (its cancel, its 30 s, and for a read its socket closing) and the socket it came on.</summary>
public sealed record CallContext(CancellationToken Cancellation, string Connection);
