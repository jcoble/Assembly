using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Assembly.Domain.Events;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>
/// A tool call that hasn't finished: everything its updates have said so far, merged, and what its last row
/// and detail said, so a row goes out only when the line changes.
/// </summary>
internal sealed class OpenTool(string itemId, string? runId = null, string? sessionFolder = null)
{
    private ToolCallFields _fields = new();
    private string? _lastLine;
    private byte[] _lastDetail = DetailFingerprint(new ToolDetail(itemId));
    private bool _hasTerminal;

    public string ItemId { get; } = itemId;
    public string? RunId { get; } = runId;
    public string? AcpSessionId => _fields.AcpSessionId;

    public bool IsDone => _fields.Status is "completed" or "failed" or "cancelled";

    public void Apply(ToolCallFields update)
    {
        _hasTerminal |= update.TerminalIds is { Count: > 0 };
        _fields = update.Snapshot ? update : new ToolCallFields(
            update.Name ?? _fields.Name,
            update.Kind ?? _fields.Kind,
            update.Title ?? _fields.Title,
            update.Status ?? _fields.Status,
            update.Input ?? _fields.Input,
            update.Output ?? _fields.Output,
            update.ContentReplaced ? update.ContentText : update.ContentText ?? _fields.ContentText,
            update.ContentReplaced ? update.Diffs : update.Diffs ?? _fields.Diffs,
            update.Paths ?? _fields.Paths, TerminalIds: update.TerminalIds ?? _fields.TerminalIds,
            AcpSessionId: update.AcpSessionId ?? _fields.AcpSessionId);
    }

    /// <summary>
    /// The row and detail to write now, if anything changed since the last ones. <paramref name="failed"/> marks a call
    /// that was still open when its run ended.
    /// </summary>
    public (ToolPayload? Line, ToolDetail? Detail) TakeChanges(bool failed = false)
    {
        var diff = UnifiedDiff.Of(_fields.Diffs);
        var input = _fields.Input is { } fields && (fields.ValueKind != JsonValueKind.Object || fields.EnumerateObject().Any()) ? fields.GetRawText() : null;
        // Some agents fill the shown text with a description while the tool runs, so it counts as output only at the end.
        var output = _fields.Output ?? (IsDone ? _fields.ContentText : null);
        var path = ToolSummary.Path(_fields.Input, sessionFolder)
            ?? ToolSummary.InFolder(_fields.Diffs?.SelectMany(diff => diff.Paths).FirstOrDefault() ?? _fields.Paths?[0], _fields.Input, sessionFolder);
        var detail = new ToolDetail(ItemId, input, output, diff?.Text, path,
            TerminalIds: _fields.TerminalIds, AcpSessionId: _fields.AcpSessionId,
            OutputSource: _fields.Output is not null ? "raw" : output is not null ? "content" : null);

        var state = failed || _fields.Status == "failed" ? "failed" : _fields.Status is "completed" or "cancelled" ? _fields.Status : "started";
        var name = _fields.Name ?? _fields.Title ?? (_fields.Kind == "other" ? null : _fields.Kind) ?? "Tool";
        var summary = ToolSummary.Of(_fields.Input) ?? _fields.Title;
        var line = new ToolPayload(name, state, summary == name ? null : summary, path, diff?.Added, diff?.Removed,
            HasDetail: input is not null || output is not null || diff is not null || _hasTerminal, _fields.Kind, diff is { Files.Count: > 1 } ? diff.Files : null);

        var lineJson = JsonSerializer.Serialize(line, RowJsonContext.Rows.ToolPayload);
        var changedLine = lineJson == _lastLine ? null : line;
        var fingerprint = DetailFingerprint(detail);
        var changedDetail = fingerprint.AsSpan().SequenceEqual(_lastDetail) ? null : detail with { Replace = true };
        (_lastLine, _lastDetail) = (lineJson, fingerprint);
        return (changedLine, changedDetail);
    }

    // V2 sends complete resolved snapshots on later patches. Finished routing needs no second copy of their detail.
    // Legacy partial producers still need their original merge fields.
    public void ReleaseCompletedSnapshot()
    {
        if (_fields.Snapshot)
            _fields = _fields with { Input = null, Output = null, ContentText = null, Diffs = null, Paths = null };
    }

    private static byte[] DetailFingerprint(ToolDetail detail)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string? text)
        {
            hash.AppendData(BitConverter.GetBytes(text?.Length ?? -1));
            if (text is not null) hash.AppendData(Encoding.UTF8.GetBytes(text));
        }
        Append(detail.Input); Append(detail.Output); Append(detail.Diff); Append(detail.Path);
        Append(detail.AcpSessionId); Append(detail.OutputSource);
        hash.AppendData(BitConverter.GetBytes(detail.TerminalIds?.Count ?? -1));
        if (detail.TerminalIds is { } ids)
            foreach (var id in ids) Append(id);
        return hash.GetHashAndReset();
    }
}
