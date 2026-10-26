namespace Assembly.Domain.Rows;

/// <summary>Background work, as <see cref="Events.TaskChanged"/> last left it.</summary>
public sealed record TaskPayload(string Name, string State, bool CanStop, string? ToolCallId);
