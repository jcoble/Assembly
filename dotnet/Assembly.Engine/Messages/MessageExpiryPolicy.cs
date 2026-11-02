using Assembly.Application.Common.Calls;
using Assembly.Application.Common.Database;

namespace Assembly.Engine.Messages;

/// <summary>Snapshots the receiving host's policy only when a fresh pending message is inserted.</summary>
public sealed class MessageExpiryPolicy(Func<IReadScope, int>? readMinutes = null)
{
    public long Deadline(IReadScope scope, long createdMs)
    {
        var minutes = readMinutes?.Invoke(scope) ?? 30;
        if (minutes <= 0) throw new ArgumentOutOfRangeException(nameof(minutes));
        return Calculate(createdMs, minutes);
    }

    internal static long Calculate(long createdMs, int minutes)
    {
        if (minutes <= 0) throw new ArgumentOutOfRangeException(nameof(minutes));
        try { return checked(createdMs + checked((long)minutes * 60_000L)); }
        catch (OverflowException) { throw new CallFailed("message_expiry_overflow"); }
    }
}
