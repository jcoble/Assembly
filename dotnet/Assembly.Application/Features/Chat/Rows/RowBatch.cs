using Assembly.Domain.Rows;
using Assembly.Domain.Events;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>What one event adds: rows, and tool details. They are committed together.</summary>
public sealed class RowBatch
{
    public List<NewRow> Rows { get; } = [];

    public List<ToolDetail> Details { get; } = [];
    public List<TerminalChanged> Terminals { get; } = [];

    public bool IsEmpty => Rows.Count == 0 && Details.Count == 0 && Terminals.Count == 0;
}
