using System.Text;
using Assembly.Application.Features.Chat.Rows.Markdown;
using Assembly.Domain.Markdown;
using Assembly.Domain.Rows;

namespace Assembly.Application.Features.Chat.Rows;

/// <summary>A message or thinking still streaming: its text so far, and how much of it has gone out in rows.</summary>
internal sealed class StreamingText(string itemId)
{
    private readonly StringBuilder _text = new();
    private IReadOnlyList<string> _shownBlocks = [];
    private int _sent;
    private long? _lastRowAt;

    public string ItemId { get; } = itemId;

    public string Text => _text.ToString();

    public bool HasUnsent => _sent < _text.Length;

    public void Append(string text) => _text.Append(text);

    /// <summary>The first row goes out at once; after that, at most one per <see cref="RowConverter.CoalesceMs"/>.</summary>
    public bool IsDue(long at) => _lastRowAt is not { } last || at - last >= RowConverter.CoalesceMs;

    /// <summary>
    /// The message's delta since the last one, or null when no block changed (whitespace, say). Held-back text
    /// isn't lost: it rides along with the next delta.
    /// </summary>
    public DeltaPayload? TakeDelta(long at)
    {
        var text = Text;
        var blocks = SafeMarkdown.Parse(text);
        var blockJson = blocks.Select(SafeMarkdown.ToJson).ToList();
        var from = _shownBlocks.Zip(blockJson).TakeWhile(pair => pair.First == pair.Second).Count();
        if (from == blockJson.Count && from == _shownBlocks.Count)
            return null;

        var delta = new DeltaPayload(from, blocks.Skip(from).ToList(), text[_sent..]);
        (_shownBlocks, _sent, _lastRowAt) = (blockJson, text.Length, at);
        return delta;
    }

    /// <summary>Thinking goes out whole each time.</summary>
    public ThinkingPayload TakeThinking(long at)
    {
        (_sent, _lastRowAt) = (_text.Length, at);
        return new ThinkingPayload(Text, Done: false);
    }
}
