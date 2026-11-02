using Assembly.Application.Features.Chat.Sessions;

namespace Assembly.Tests;

internal static class SessionSendTestExtensions
{
    // Existing event/open tests intentionally start the pending operation without awaiting provider insertion.
    // The production CurrentTurn capture is also used by LocalMachine to wait for that exact turn.
    public static string? Send(this SessionHost host, string text, string? clientId = null)
    {
        var sending = host.SendAsync(text, clientId, null, CancellationToken.None);
        return sending.IsCompletedSuccessfully && sending.Result is SendResult.Busy ? null : host.CurrentTurn.RunId;
    }
}
