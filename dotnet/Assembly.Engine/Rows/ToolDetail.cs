namespace Assembly.Engine.Rows;

/// <summary>
/// What an opened tool row shows beyond its one line. Null keeps what is already stored.
/// <c>Input</c> is the call's arguments as JSON text, and <c>Diff</c> is a unified diff.
/// </summary>
public sealed record ToolDetail(string ItemId, string? Input = null, string? Output = null, string? Diff = null, string? Path = null);
