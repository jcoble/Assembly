namespace Assembly.Engine.Events;

/// <summary>
/// A tool call started (<c>Started</c>) or changed. Fields left null didn't change.
/// </summary>
public sealed record ToolCallChanged(long At, string ToolCallId, bool Started, ToolCallFields Fields) : AgentEvent(At);
