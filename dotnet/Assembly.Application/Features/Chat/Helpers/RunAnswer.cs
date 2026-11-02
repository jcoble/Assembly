using Assembly.Application.Features.Chat.Rows;

namespace Assembly.Application.Features.Chat.Helpers;

/// <summary>How a run that took a message ended: answered or failed, its last reply, and its error if any.</summary>
public sealed record RunAnswer(string State, string Reply, string? Error)
{
    /// <summary>The run's answer from the session's rows here, or null while it hasn't ended.</summary>
    public static RunAnswer? Of(LiveHub hub, string sessionId, string runId)
    {
        var reply = hub.TerminalReply(sessionId, runId);
        return reply is null ? null : new RunAnswer(reply.TerminalState == "finished" ? "answered" : "failed", reply.Text, reply.Error);
    }
}
