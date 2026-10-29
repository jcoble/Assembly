using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Domain.Sessions;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>
/// A helper's row in its parent's session: running while it works, idle once its turn ends, as stopped or failed when
/// that turn was.
/// </summary>
public sealed class HelperRow(SessionStore sessionStore, SessionRegistry sessions, LiveHub hub)
{
    public void Show(SessionInfo helper, string state, string? stopReason = null, string? error = null)
    {
        // A helper's row is shown only by the machine that runs its parent: a parent on another machine commits it
        // there, and this machine copies it.
        if (helper.ParentSessionId is not { } parent || sessionStore.Get(parent) is not { Machine: null })
            return;
        var payload = new SubagentPayload(helper.SessionId, helper.Title ?? "Session", $"Session ({helper.Agent})", state, stopReason, error,
            CanStop: state != "idle", ToolCallId: null, Agent: helper.Agent, Helper: true);
        // The ACP-id slot carries Assembly's session id here, not an ACP id.
        sessions.For(parent).Show(new SubagentShown(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), helper.SessionId, payload));
    }

    /// <summary>A turn ended in <paramref name="sessionId"/>: if it's a helper, its row says so.</summary>
    public void TurnEnded(string sessionId)
    {
        if (sessionStore.Get(sessionId) is not { Helper: true } helper)
            return;
        // How its last turn ended, from its rows here (copied, for a helper on another machine).
        var rows = hub.Page(sessionId, long.MaxValue, 50).Rows;
        var end = rows.LastOrDefault(row => row.Kind == RowKind.Turn)?.Payload.GetProperty("state").GetString();
        var error = end == "failed" ? rows.LastOrDefault(row => row.Kind == RowKind.Error)?.Payload.GetProperty("message").GetString() ?? "It failed." : null;
        Show(helper, "idle", end == "cancelled" ? "cancelled" : null, error);
    }
}
