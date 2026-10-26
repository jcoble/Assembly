using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Rows;
using Assembly.Domain.Rows;
using Assembly.Application.Common.Agents;
using Assembly.Application.Features.Chat.Sessions;
using Assembly.Application.Features.Chat.Threads;
using Assembly.Domain.Threads;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>
/// A helper's row in its parent's thread: running while it works, idle once its turn ends, as stopped or failed when
/// that turn was.
/// </summary>
public sealed class HelperRow(ThreadStore threads, SessionRegistry sessions, LiveHub hub)
{
    public void Show(ThreadInfo helper, string state, string? stopReason = null, string? error = null)
    {
        // A helper started from another machine has its row there.
        if (helper.ParentThreadId is not { } parent || threads.Get(parent) is null)
            return;
        var payload = new SubagentPayload(helper.ThreadId, helper.Title ?? "Helper", $"Helper session ({helper.Agent})", state, stopReason, error,
            CanStop: state != "idle", ToolCallId: null, Agent: helper.Agent, Helper: true);
        sessions.For(parent).Show(new SubagentShown(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), helper.ThreadId, payload));
    }

    /// <summary>A turn ended in <paramref name="threadId"/>: if it's a helper, its row says so.</summary>
    public void TurnEnded(string threadId)
    {
        if (threads.Get(threadId) is not { Helper: true } helper)
            return;
        // How its last turn ended, from its rows here (copied, for a helper on another machine).
        var rows = hub.Page(threadId, long.MaxValue, 50).Rows;
        var end = rows.LastOrDefault(row => row.Kind == RowKind.Turn)?.Payload.GetProperty("state").GetString();
        var error = end == "failed" ? rows.LastOrDefault(row => row.Kind == RowKind.Error)?.Payload.GetProperty("message").GetString() ?? "It failed." : null;
        Show(helper, "idle", end == "cancelled" ? "cancelled" : null, error);
    }
}
