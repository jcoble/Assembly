using System.Text.Json;
using Assembly.Engine.Events;
using Assembly.Engine.Rows.Payloads;

namespace Assembly.Engine.Rows;

/// <summary>
/// A tool call that hasn't finished: everything its updates have said so far, merged, and what its last row
/// and detail said, so a row goes out only when the line changes.
/// </summary>
internal sealed class OpenTool(string itemId)
{
    private ToolCallFields _fields = new();
    private string? _lastLine;
    private ToolDetail _lastDetail = new(itemId);

    public string ItemId { get; } = itemId;

    public bool IsDone => _fields.Status is "completed" or "failed";

    public void Apply(ToolCallFields update) => _fields = new ToolCallFields(
        update.Name ?? _fields.Name,
        update.Kind ?? _fields.Kind,
        update.Title ?? _fields.Title,
        update.Status ?? _fields.Status,
        update.Input ?? _fields.Input,
        update.Output ?? _fields.Output,
        update.ContentText ?? _fields.ContentText,
        update.Diffs ?? _fields.Diffs,
        update.Paths ?? _fields.Paths);

    /// <summary>
    /// The row and detail to write now, if anything changed since the last ones. <paramref name="failed"/> marks a call
    /// that was still open when its run ended.
    /// </summary>
    public (ToolPayload? Line, ToolDetail? Detail) TakeChanges(bool failed = false)
    {
        var diff = UnifiedDiff.Of(_fields.Diffs);
        var input = _fields.Input is { } fields && fields.EnumerateObject().Any() ? fields.GetRawText() : null;
        // Some agents fill the shown text with a description while the tool runs, so it counts as output only at the end.
        var output = _fields.Output ?? (IsDone ? _fields.ContentText : null);
        var path = ToolSummary.Path(_fields.Input) ?? _fields.Diffs?[0].Path ?? _fields.Paths?[0];
        var detail = new ToolDetail(ItemId, input, output, diff?.Text, path);

        var state = failed || _fields.Status == "failed" ? "failed" : _fields.Status == "completed" ? "completed" : "started";
        var name = _fields.Name ?? _fields.Title ?? _fields.Kind ?? "Tool";
        var summary = ToolSummary.Of(_fields.Input) ?? _fields.Title;
        var line = new ToolPayload(name, state, summary == name ? null : summary, path, diff?.Added, diff?.Removed,
            HasDetail: input is not null || output is not null || diff is not null, _fields.Kind);

        var lineJson = JsonSerializer.Serialize(line, RowJsonContext.Rows.ToolPayload);
        var changedLine = lineJson == _lastLine ? null : line;
        var changedDetail = new ToolDetail(ItemId,
            Changed(input, _lastDetail.Input), Changed(output, _lastDetail.Output), Changed(diff?.Text, _lastDetail.Diff), Changed(path, _lastDetail.Path));
        (_lastLine, _lastDetail) = (lineJson, detail);
        return (changedLine, changedDetail is { Input: null, Output: null, Diff: null, Path: null } ? null : changedDetail);
    }

    private static string? Changed(string? now, string? before) => now == before ? null : now;
}
