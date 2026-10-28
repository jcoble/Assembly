namespace Assembly.Domain.Rows;

/// <summary>One file a tool call changed, and how many of its lines it adds and removes.</summary>
public sealed record FileChange(string Path, int Added, int Removed);
