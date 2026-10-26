using Assembly.Domain.Markdown;

namespace Assembly.Domain.Rows;

/// <summary>
/// A message still streaming. <c>From</c> is the first block that changed, and <c>Blocks</c> replaces the shown
/// blocks from there on. <c>Text</c> is only the raw text this delta adds, so a page can rebuild an unfinished message.
/// </summary>
public sealed record DeltaPayload(int From, IReadOnlyList<SafeBlock> Blocks, string Text);
