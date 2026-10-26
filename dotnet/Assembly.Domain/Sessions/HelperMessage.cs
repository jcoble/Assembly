namespace Assembly.Domain.Sessions;

/// <summary>
/// A message one thread's agent sent another's through the Assembly MCP server, kept on the sending machine.
/// <c>RunId</c> is the run in the receiving thread that it went into, once delivered; <c>Error</c> says why it couldn't
/// be. A message to a thread on another machine is answered from there: <c>State</c> (answered or failed) and
/// <c>Reply</c>. Times are Unix milliseconds.
/// </summary>
public sealed record HelperMessage(string MessageId, string From, string To, string Text, string? RunId, long CreatedAt, string? Error = null,
    string? State = null, string? Reply = null);
