namespace Assembly.Application.Common.Calls;

/// <summary>
/// One call's token (its cancel, its 30 s, and for a read its socket closing), the socket it came on, and that socket's
/// closing, which a write can check.
/// </summary>
public sealed record CallContext(CancellationToken Cancellation, string Connection, CancellationToken Closed = default,
    long? ReplyId = null, CallPeer? Peer = null, ExpectedSource? ExpectedSource = null)
{
    // Installed only by the server's handler, never read from client JSON. The socket owns disposal after reply.
    public IExpectedSourceGuard? SourceGuard { get; set; }
}
