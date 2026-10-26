using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Assembly.Domain.Events;
using Assembly.Application.Features.Chat.Helpers;
using Assembly.Application.Features.Chat.Rows.Markdown;
using Assembly.Domain.Markdown;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// One session's events to rows. It keeps only what is still open: the streaming message, the thinking,
/// the tool calls not yet finished, and the turn's tallies. The same events always give the same rows,
/// because time comes from the events, so the Mac and Workbox convert alike.
/// </summary>
public sealed class RowConverter
{
    /// <summary>Streaming text becomes a row at most this often, in event time.</summary>
    public const long CoalesceMs = 100;

    private readonly OrderedDictionary<string, OpenTool> _tools = new();
    // This turn's finished tool calls, so an update that arrives after the end adds nothing.
    private readonly HashSet<string> _finishedTools = [];
    // Tools a turn's end marked failed while they were still running, by id.
    private readonly Dictionary<string, OpenTool> _endedTools = [];
    // How many times each agent message id has been opened, so text or thinking that resumes after a tool is its own item.
    private readonly Dictionary<string, int> _segments = new();
    private OpenTurn? _turn;
    // The run that ended last: a late update to one of its tool calls, after its end, is still its row.
    private string? _lastRunId;
    private StreamingText? _message;
    private string? _messageAgentId;
    private StreamingText? _thinking;
    private decimal? _sessionCost;
    private int _unnamedItems;
    // The run each background task started in, so its later rows stay with it after that run ends.
    private readonly Dictionary<string, string?> _taskRuns = new();
    // The run each subagent's row went into first.
    private readonly Dictionary<string, string?> _subagentRuns = new();
    // The last context pair written, and in which run: an unchanged report adds no row.
    private (string? RunId, long Used, long Size)? _context;

    /// <summary>The session's total cost as the agent last reported it, or null before any report.</summary>
    public decimal? SessionCost => _sessionCost;

    /// <summary>Whether this session made the tool call, still running or finished this turn.</summary>
    public bool KnowsTool(string toolCallId) => _tools.ContainsKey(toolCallId) || _finishedTools.Contains(toolCallId);

    /// <summary>Whether a tool call is still running.</summary>
    public bool HasOpenTools => _tools.Count > 0;

    public RowBatch Apply(AgentEvent agentEvent)
    {
        var batch = new RowBatch();
        switch (agentEvent)
        {
            case TurnStarted started:
                StartTurn(batch, started);
                break;
            case RunStarted started:
                // The agent went on by itself: a turn with no message of the user's.
                _turn = new OpenTurn(started.RunId, started.At, _sessionCost);
                Add(batch, null, RowKind.Turn, new TurnPayload("started", started.Reason), RowJsonContext.Rows.TurnPayload, started.At);
                break;
            case TaskChanged task:
                var runId = _taskRuns.TryGetValue(task.TaskId, out var startedIn) ? startedIn : _taskRuns[task.TaskId] = _turn?.RunId ?? _lastRunId;
                var payload = new TaskPayload(task.Name, task.State, task.CanStop, task.ToolCallId);
                batch.Rows.Add(new NewRow(runId, $"task:{task.TaskId}", RowKind.Task,
                    JsonSerializer.SerializeToElement(payload, RowJsonContext.Rows.TaskPayload), task.At));
                if (!task.Running)
                    _taskRuns.Remove(task.TaskId);
                break;
            case SubagentShown shown:
                // A subagent's row stays with the run that started it, each time it's delegated to again.
                var subagentRun = _subagentRuns.TryGetValue(shown.AcpSessionId, out var subagentStartedIn)
                    ? subagentStartedIn
                    : _subagentRuns[shown.AcpSessionId] = _turn?.RunId ?? _lastRunId;
                batch.Rows.Add(new NewRow(subagentRun, $"subagent:{shown.AcpSessionId}", RowKind.Subagent,
                    JsonSerializer.SerializeToElement(shown.Subagent, RowJsonContext.Rows.SubagentPayload), shown.At));
                break;
            case TurnSteered steered:
                // The agent's answer so far is finished; what follows answers the new message too.
                CloseThinking(batch, steered.At);
                CloseMessage(batch, steered.At);
                Add(batch, null, RowKind.User, new UserPayload(steered.Text, MessageFrame.SenderOf(steered.Text)), RowJsonContext.Rows.UserPayload, steered.At);
                break;
            case TextChunk chunk:
                AddText(batch, chunk);
                break;
            case ThoughtChunk chunk:
                AddThought(batch, chunk);
                break;
            case ToolCallChanged change:
                ChangeTool(batch, change);
                break;
            case PlanChanged plan:
                var items = plan.Entries.Select(entry => new PlanItem(entry.Text, entry.Status)).ToList();
                Add(batch, $"{_turn?.RunId}:plan", RowKind.Plan, new PlanPayload(items), RowJsonContext.Rows.PlanPayload, plan.At);
                break;
            case AskChanged ask:
                if (ask.Ask.State == "open")
                {
                    CloseThinking(batch, ask.At);
                    CloseMessage(batch, ask.At);
                }
                Add(batch, $"ask:{ask.AskId}", RowKind.Ask, ask.Ask, RowJsonContext.Rows.AskPayload, ask.At);
                break;
            case AgentStarted started:
                // The session's agent sums its cost itself. A new session starts from zero; a resumed one goes on
                // from the total saved at its last clean close, or from a total the rows never saw.
                _sessionCost = started.Resumed ? started.SavedCost : null;
                _turn?.AgentStarted(started.Resumed, started.SavedCost);
                if (_turn is not null)
                    _turn.Model = started.Model;
                break;
            case UsageReported usage:
                _sessionCost = usage.CostUsd ?? _sessionCost;
                if (_turn is not null)
                    _turn.Model = usage.Model ?? _turn.Model;
                AddContext(batch, usage);
                break;
            case TurnEnded ended:
                EndTurn(batch, ended.At, ended.StopReason == "cancelled" ? "cancelled" : "finished", ended.Usage, error: null);
                break;
            case TurnFailed failed:
                EndTurn(batch, failed.At, "failed", usage: null, new ErrorPayload(failed.Message, failed.Code));
                break;
            case AgentLost lost:
                EndTurn(batch, lost.At, "failed", usage: null, new ErrorPayload("The agent stopped before the turn finished.", "agent_lost"));
                break;
            case ClockTick tick:
                FlushDue(batch, tick.At);
                break;
        }

        return batch;
    }

    private void StartTurn(RowBatch batch, TurnStarted started)
    {
        _turn = new OpenTurn(started.RunId, started.At, _sessionCost);
        Add(batch, null, RowKind.User, new UserPayload(started.Text, MessageFrame.SenderOf(started.Text)), RowJsonContext.Rows.UserPayload, started.At);
        Add(batch, null, RowKind.Turn, new TurnPayload("started"), RowJsonContext.Rows.TurnPayload, started.At);
    }

    private void AddText(RowBatch batch, TextChunk chunk)
    {
        CloseThinking(batch, chunk.At);
        if (_message is not null && chunk.MessageId is not null && chunk.MessageId != _messageAgentId)
            CloseMessage(batch, chunk.At);
        if (_message is null)
            (_message, _messageAgentId) = (new StreamingText(SegmentId(chunk.MessageId, "")), chunk.MessageId);
        if (_turn is not null)
            _turn.FirstTextAt ??= chunk.At;

        _message.Append(chunk.Text);
        if (_message.IsDue(chunk.At))
            SendDelta(batch, chunk.At);
    }

    private void AddThought(RowBatch batch, ThoughtChunk chunk)
    {
        CloseMessage(batch, chunk.At);
        _thinking ??= new StreamingText(SegmentId(chunk.MessageId, ":thinking"));
        _thinking.Append(chunk.Text);
        if (_thinking.IsDue(chunk.At))
            SendThinking(batch, chunk.At);
    }

    private void ChangeTool(RowBatch batch, ToolCallChanged change)
    {
        if (_finishedTools.Contains(change.ToolCallId))
            return;
        // A tool the turn's end failed may still finish (a backgrounded command); any other word on it after the end
        // (Codex's bare update for a command stopped as the turn ended) leaves it as it ended.
        if (_endedTools.TryGetValue(change.ToolCallId, out var ended))
        {
            if (change.Fields.Status is not ("completed" or "failed"))
                return;
            _endedTools.Remove(change.ToolCallId);
            _tools[change.ToolCallId] = ended;
        }
        if (change.Started || !_tools.ContainsKey(change.ToolCallId))
        {
            // What the agent said before it reached for a tool is a finished message.
            CloseMessage(batch, change.At);
            CloseThinking(batch, change.At);
            _tools[change.ToolCallId] = new OpenTool(change.ToolCallId);
            if (_turn is not null)
                _turn.ToolCalls++;
        }

        var tool = _tools[change.ToolCallId];
        tool.Apply(change.Fields);
        WriteTool(batch, tool, change.At, failed: false);
        if (tool.IsDone)
        {
            _tools.Remove(tool.ItemId);
            _finishedTools.Add(tool.ItemId);
        }
    }

    private void EndTurn(RowBatch batch, long at, string state, TokenUsage? usage, ErrorPayload? error)
    {
        // A run's end closes what it left open: the message keeps its text, thinking is done, and a tool still running failed.
        CloseThinking(batch, at);
        CloseMessage(batch, at);
        foreach (var tool in _tools.Values)
        {
            WriteTool(batch, tool, at, failed: true);
            _endedTools[tool.ItemId] = tool;
        }
        _tools.Clear();
        _finishedTools.Clear();
        _segments.Clear();

        if (error is not null)
            Add(batch, null, RowKind.Error, error, RowJsonContext.Rows.ErrorPayload, at);
        if (_turn is not null)
        {
            var status = state switch { "finished" => "completed", "cancelled" => "aborted", _ => "failed" };
            Add(batch, null, RowKind.Stats, _turn.Stats(status, at, usage, _sessionCost), RowJsonContext.Rows.StatsPayload, at);
        }

        Add(batch, null, RowKind.Turn, new TurnPayload(state), RowJsonContext.Rows.TurnPayload, at);
        _lastRunId = _turn?.RunId;
        _turn = null;
    }

    private void FlushDue(RowBatch batch, long at)
    {
        if (_message is { HasUnsent: true } && _message.IsDue(at))
            SendDelta(batch, at);
        if (_thinking is { HasUnsent: true } && _thinking.IsDue(at))
            SendThinking(batch, at);
    }

    private void SendDelta(RowBatch batch, long at)
    {
        if (_message!.TakeDelta(at) is { } delta)
            Add(batch, _message.ItemId, RowKind.Delta, delta, RowJsonContext.Rows.DeltaPayload, at);
    }

    private void SendThinking(RowBatch batch, long at) =>
        Add(batch, _thinking!.ItemId, RowKind.Thinking, _thinking.TakeThinking(at), RowJsonContext.Rows.ThinkingPayload, at);

    private void CloseMessage(RowBatch batch, long at)
    {
        if (_message is null)
            return;
        var blocks = SafeMarkdown.Parse(_message.Text);
        Add(batch, _message.ItemId, RowKind.Message, new MessagePayload(blocks), RowJsonContext.Rows.MessagePayload, at);
        _message = null;
    }

    private void CloseThinking(RowBatch batch, long at)
    {
        if (_thinking is null)
            return;
        Add(batch, _thinking.ItemId, RowKind.Thinking, new ThinkingPayload(_thinking.Text, Done: true), RowJsonContext.Rows.ThinkingPayload, at);
        _thinking = null;
    }

    private void WriteTool(RowBatch batch, OpenTool tool, long at, bool failed)
    {
        var (line, detail) = tool.TakeChanges(failed);
        if (line is not null)
            Add(batch, tool.ItemId, RowKind.Tool, line, RowJsonContext.Rows.ToolPayload, at);
        if (detail is not null)
            batch.Details.Add(detail);
    }

    /// <summary>The item id for text (<paramref name="suffix"/> empty) or thinking: the agent's message id, numbered after the first.</summary>
    private string SegmentId(string? agentMessageId, string suffix)
    {
        if (agentMessageId is null)
            return NextUnnamed() + suffix;
        var key = agentMessageId + suffix;
        var segment = _segments[key] = _segments.GetValueOrDefault(key) + 1;
        return segment == 1 ? key : $"{key}:{segment}";
    }

    private string NextUnnamed() => $"{_turn?.RunId}:{++_unnamedItems}";

    // One context item per run, so the newest page holds the latest one.
    private void AddContext(RowBatch batch, UsageReported usage)
    {
        if (usage is not { Used: { } used, Size: { } size } || size <= 0 || (_turn?.RunId ?? _lastRunId) is not { } runId
            || _context == (runId, used, size))
            return;
        _context = (runId, used, size);
        Add(batch, $"context:{runId}", RowKind.Context, new ContextPayload(used, size), RowJsonContext.Rows.ContextPayload, usage.At);
    }

    private void Add<T>(RowBatch batch, string? itemId, string kind, T payload, JsonTypeInfo<T> type, long at) =>
        batch.Rows.Add(new NewRow(_turn?.RunId ?? _lastRunId, itemId, kind, JsonSerializer.SerializeToElement(payload, type), at));
}
