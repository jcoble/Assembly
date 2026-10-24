using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Engine.Live;
using Assembly.Engine.Markdown;
using Assembly.Engine.Rows.Payloads;
using Assembly.Engine.Store;

namespace Assembly.Engine.Rows;

/// <summary>
/// Closes a run the backend stopped in the middle of, from its saved rows alone, on the next start. It closes what
/// a lost agent's end would: a streaming message keeps the text that was saved, thinking is done, a tool still
/// running failed, and a card still waiting on the user is cancelled; background tasks still running stopped. Then an error and <c>turn: failed</c>. No stats
/// row: the turn's tallies died with the backend.
/// </summary>
public static class RunRecovery
{
    public const string Code = "backend_stopped";

    /// <summary>
    /// Closes every turn the backend's last run left running, so no thread looks busy forever. Returns how many. Only
    /// threads whose agent <paramref name="runsHere"/>: a remote thread's turn is the remote machine's to close, and
    /// its rows arrive by copy.
    /// </summary>
    public static int CloseUnfinished(RowStore store, LiveHub hub, long at, Func<string, bool> runsHere)
    {
        var runs = store.UnfinishedRuns().Where(run => runsHere(run.ThreadId)).ToList();
        foreach (var (threadId, runId) in runs)
            hub.Publish(threadId, Close(store.RunRows(threadId, runId), at));
        // A session kept open for background work outlives its run, so its tasks are closed on their own.
        foreach (var task in store.RunningItems(RowKind.Task).Where(task => runsHere(task.ThreadId)))
        {
            var batch = new RowBatch();
            var payload = task.Payload.Deserialize(RowJsonContext.Rows.TaskPayload)!;
            Add(batch, task.RunId, task.ItemId, RowKind.Task, payload with { State = "stopped" }, RowJsonContext.Rows.TaskPayload, at);
            hub.Publish(task.ThreadId, batch);
        }
        // And so are subagents: each child's own thread had its run closed above.
        foreach (var subagent in store.RunningItems(RowKind.Subagent).Where(subagent => runsHere(subagent.ThreadId)))
        {
            var batch = new RowBatch();
            var payload = subagent.Payload.Deserialize(RowJsonContext.Rows.SubagentPayload)!;
            Add(batch, subagent.RunId, subagent.ItemId, RowKind.Subagent,
                payload with { State = "idle", StopReason = "cancelled", CanStop = false }, RowJsonContext.Rows.SubagentPayload, at);
            hub.Publish(subagent.ThreadId, batch);
        }
        return runs.Count;
    }

    public static RowBatch Close(IReadOnlyList<StoredRow> runRows, long at)
    {
        var batch = new RowBatch();
        if (runRows.Count == 0)
            return batch;
        var runId = runRows[0].RunId;
        var items = runRows.Where(row => row.ItemId is not null).ToList();

        // The same order as the converter's run end: thinking, then the message, then tools.
        foreach (var thinking in Newest(items, RowKind.Thinking))
        {
            var payload = thinking.Payload.Deserialize(RowJsonContext.Rows.ThinkingPayload)!;
            if (!payload.Done)
                Add(batch, runId, thinking.ItemId, RowKind.Thinking, payload with { Done = true }, RowJsonContext.Rows.ThinkingPayload, at);
        }

        var finished = items.Where(row => row.Kind == RowKind.Message).Select(row => row.ItemId).ToHashSet();
        foreach (var streaming in items.Where(row => row.Kind == RowKind.Delta && !finished.Contains(row.ItemId)).GroupBy(row => row.ItemId))
        {
            var text = string.Concat(streaming.Select(delta => delta.Payload.GetProperty("text").GetString()));
            Add(batch, runId, streaming.Key, RowKind.Message, new MessagePayload(SafeMarkdown.Parse(text)), RowJsonContext.Rows.MessagePayload, at);
        }

        foreach (var tool in Newest(items, RowKind.Tool))
        {
            var payload = tool.Payload.Deserialize(RowJsonContext.Rows.ToolPayload)!;
            if (payload.State == "started")
                Add(batch, runId, tool.ItemId, RowKind.Tool, payload with { State = "failed" }, RowJsonContext.Rows.ToolPayload, at);
        }

        foreach (var ask in Newest(items, RowKind.Ask))
        {
            var payload = ask.Payload.Deserialize(RowJsonContext.Rows.AskPayload)!;
            if (payload.State == "open")
                Add(batch, runId, ask.ItemId, RowKind.Ask, payload with { State = "cancelled" }, RowJsonContext.Rows.AskPayload, at);
        }

        Add(batch, runId, null, RowKind.Error, new ErrorPayload("The backend stopped before the turn finished.", Code), RowJsonContext.Rows.ErrorPayload, at);
        Add(batch, runId, null, RowKind.Turn, new TurnPayload("failed"), RowJsonContext.Rows.TurnPayload, at);
        return batch;
    }

    /// <summary>Each item's newest row of the kind, in the order the items started.</summary>
    private static IEnumerable<StoredRow> Newest(List<StoredRow> items, string kind) =>
        items.Where(row => row.Kind == kind).GroupBy(row => row.ItemId).Select(group => group.Last());

    private static void Add<T>(RowBatch batch, string? runId, string? itemId, string kind, T payload, JsonTypeInfo<T> type, long at) =>
        batch.Rows.Add(new NewRow(runId, itemId, kind, JsonSerializer.SerializeToElement(payload, type), at));
}
