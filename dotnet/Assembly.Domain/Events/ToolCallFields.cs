using System.Text.Json;

namespace Assembly.Domain.Events;

/// <summary>
/// What a tool call update says. Snapshot replaces all fields, including null clears; otherwise null means omitted,
/// except ContentText/Diffs when ContentReplaced is true. Terminal statuses are completed, failed and cancelled.
/// <c>Output</c> is the tool's own output; <c>ContentText</c> is the text the agent shows for it, which is
/// sometimes only a description until the tool finishes.
/// </summary>
public sealed record ToolCallFields(
    string? Name = null,
    string? Kind = null,
    string? Title = null,
    string? Status = null,
    JsonElement? Input = null,
    string? Output = null,
    string? ContentText = null,
    IReadOnlyList<FileDiff>? Diffs = null,
    IReadOnlyList<string>? Paths = null,
    bool ContentReplaced = false,
    IReadOnlyList<string>? TerminalIds = null,
    string? AcpSessionId = null,
    bool Snapshot = false);
