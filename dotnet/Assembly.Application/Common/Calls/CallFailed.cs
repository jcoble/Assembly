namespace Assembly.Application.Common.Calls;

/// <summary>
/// A call that can't be done as asked; it answers <c>failed</c> with the message. An update failure
/// may name the tool it failed on (<c>adapter</c>, <c>cli:&lt;family&gt;</c>, <c>backend</c>,
/// <c>desktop</c>) so the failure can be attributed to exactly that tool, never by guessing.
/// </summary>
public sealed class CallFailed(string message, string? reason = null, string? tool = null) : Exception(message)
{
    public string? Reason { get; } = reason;
    public string? Tool { get; } = tool;
}
