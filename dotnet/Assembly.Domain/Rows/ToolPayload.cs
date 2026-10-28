namespace Assembly.Domain.Rows;

/// <summary>
/// A tool call's one line. <c>State</c> is <c>started</c>, <c>completed</c> or <c>failed</c>.
/// <c>Added</c> and <c>Removed</c> count a file change's lines. <c>HasDetail</c> says opening it shows more.
/// <c>Kind</c> is ACP's: <c>read</c>, <c>edit</c>, <c>delete</c>, <c>move</c>, <c>search</c>, <c>execute</c>, <c>think</c>,
/// <c>fetch</c> or <c>other</c>. <c>Files</c> lists each file a call changed, only when it changed more than one: then
/// <c>Path</c> is the first and <c>Added</c> and <c>Removed</c> count them all.
/// </summary>
public sealed record ToolPayload(string Name, string State, string? Summary, string? Path, int? Added, int? Removed, bool HasDetail,
    string? Kind = null, IReadOnlyList<FileChange>? Files = null);
