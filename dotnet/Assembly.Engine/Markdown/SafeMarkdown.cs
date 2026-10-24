using System.Text.Json;
using Markdig;

namespace Assembly.Engine.Markdown;

/// <summary>
/// Markdown text to safe blocks, the shape the UI draws. Parsed with Markdig, with the same extensions
/// Assembly's comrak setup uses: tables, strikethrough, task lists and bare-URL links.
/// </summary>
public static class SafeMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    public static IReadOnlyList<SafeBlock> Parse(string markdown) =>
        new BlockReader(markdown).Read(Markdig.Markdown.Parse(markdown, Pipeline));

    public static string ToJson(IReadOnlyList<SafeBlock> blocks) =>
        JsonSerializer.Serialize(blocks, MarkdownJsonContext.Default.IReadOnlyListSafeBlock);

    public static string ToJson(SafeBlock block) =>
        JsonSerializer.Serialize(block, MarkdownJsonContext.Default.SafeBlock);
}
