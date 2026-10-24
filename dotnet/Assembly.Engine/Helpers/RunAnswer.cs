using Assembly.Engine.Live;
using Assembly.Engine.Rows;

namespace Assembly.Engine.Helpers;

/// <summary>How a run that took a message ended: answered or failed, its last reply, and its error if any.</summary>
public sealed record RunAnswer(string State, string Reply, string? Error)
{
    /// <summary>The run's answer from the thread's rows here, or null while it hasn't ended.</summary>
    public static RunAnswer? Of(LiveHub hub, string threadId, string runId)
    {
        var rows = hub.RunRows(threadId, runId);
        var end = rows.LastOrDefault(row => row.Kind == RowKind.Turn);
        if (end is null || end.Payload.GetProperty("state").GetString() == "started")
            return null;
        var reply = rows.LastOrDefault(row => row.Kind == RowKind.Message) is { } last ? PlainText.Of(last.Payload.GetProperty("blocks")) : "";
        var error = rows.LastOrDefault(row => row.Kind == RowKind.Error)?.Payload.GetProperty("message").GetString();
        return new RunAnswer(end.Payload.GetProperty("state").GetString() == "finished" ? "answered" : "failed", reply, error);
    }
}
