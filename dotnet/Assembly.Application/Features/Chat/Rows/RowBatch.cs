using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>What one event adds: rows, and tool details. They are committed together.</summary>
public sealed class RowBatch
{
    public List<NewRow> Rows { get; } = [];

    public List<ToolDetail> Details { get; } = [];

    public bool IsEmpty => Rows.Count == 0 && Details.Count == 0;
}
