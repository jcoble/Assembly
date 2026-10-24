using Assembly.Engine.Markdown;

namespace Assembly.Engine.Rows.Payloads;

/// <summary>A finished message.</summary>
public sealed record MessagePayload(IReadOnlyList<SafeBlock> Blocks);
