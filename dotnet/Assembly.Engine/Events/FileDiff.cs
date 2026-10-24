namespace Assembly.Engine.Events;

/// <summary>A file change a tool made or wants to make. <c>OldText</c> is null for a new file.</summary>
public sealed record FileDiff(string Path, string? OldText, string NewText);
