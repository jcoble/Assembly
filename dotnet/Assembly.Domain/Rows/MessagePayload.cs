using Assembly.Domain.Markdown;

namespace Assembly.Domain.Rows;

/// <summary>A finished message.</summary>
public sealed record MessagePayload(IReadOnlyList<SafeBlock> Blocks);
