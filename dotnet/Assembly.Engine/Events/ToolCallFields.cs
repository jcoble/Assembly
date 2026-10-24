using System.Text.Json;

namespace Assembly.Engine.Events;

/// <summary>
/// What a tool call update says. Null means not mentioned.
/// <c>Status</c> is <c>pending</c>, <c>in_progress</c>, <c>completed</c> or <c>failed</c>.
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
    IReadOnlyList<string>? Paths = null);
