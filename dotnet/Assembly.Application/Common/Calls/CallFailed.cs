namespace Assembly.Application.Common.Calls;

/// <summary>A call that can't be done as asked; it answers <c>failed</c> with the message.</summary>
public sealed class CallFailed(string message, string? reason = null) : Exception(message)
{
    public string? Reason { get; } = reason;
}
