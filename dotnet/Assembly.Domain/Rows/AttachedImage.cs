namespace Assembly.Domain.Rows;

/// <summary>An image a prompt's trailing "Attached image:" line names: where it was saved and the name it was attached with.</summary>
public sealed record AttachedImage(string Path, string Name);
