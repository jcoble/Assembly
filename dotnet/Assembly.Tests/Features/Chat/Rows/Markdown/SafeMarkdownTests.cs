using Assembly.Application.Features.Chat.Rows.Markdown;
using Assembly.Domain.Markdown;

namespace Assembly.Tests.Features.Chat.Rows.Markdown;

/// <summary>The first seven are the Rust relay's safe_markdown tests, ported; the rest are new.</summary>
public sealed class SafeMarkdownTests
{
    private static SafeText T(string value) => new(value);
    private static SafeParagraph P(params SafeInline[] parts) => new(parts);
    private static SafeListItem Item(SafeInline[] parts, params SafeBlock[] blocks) => new(false, false, parts, blocks);
    private static SafeList Bullets(params SafeListItem[] items) => new(false, items);

    private static void AssertBlocks(string markdown, params SafeBlock[] expected) =>
        Assert.Equal(SafeMarkdown.ToJson(expected), SafeMarkdown.ToJson(SafeMarkdown.Parse(markdown)));

    [Fact]
    public void Parses_a_paragraph_with_code_and_bold() =>
        AssertBlocks("Hello world `foo` **bold**",
            P(T("Hello world "), new SafeCodeSpan("foo"), T(" "), new SafeStrong([T("bold")])));

    [Fact]
    public void Parses_web_and_file_links() =>
        AssertBlocks("[docs](https://example.com) and [code](file:///src/main.rs) and [rel](src/app.ts:42)",
            P(new SafeLink("https://example.com", [T("docs")]), T(" and "),
              new SafeFileLink("/src/main.rs", [T("code")]), T(" and "),
              new SafeFileLink("src/app.ts:42", [T("rel")])));

    [Fact]
    public void Nested_lists_and_quotes_keep_each_item_text_once() =>
        AssertBlocks(
            "1. First\n   - nested a\n     - deeper **b**\n       > quoted in a list\n   - nested c\n2. Second\n\n> Outer quote\n> > Inner quote with `code`\n> - list in quote\n",
            new SafeList(true,
            [
                Item([T("First")], Bullets(
                    Item([T("nested a")], Bullets(
                        Item([T("deeper "), new SafeStrong([T("b")])], new SafeQuote([P(T("quoted in a list"))])))),
                    Item([T("nested c")]))),
                Item([T("Second")]),
            ]),
            new SafeQuote(
            [
                P(T("Outer quote")),
                new SafeQuote([P(T("Inner quote with "), new SafeCodeSpan("code"))]),
                Bullets(Item([T("list in quote")])),
            ]));

    [Fact]
    public void Fenced_code_does_not_keep_the_newline_before_its_closing_fence()
    {
        AssertBlocks("```ts\nconst a = 1;\n```\n", new SafeCode("ts", "const a = 1;", true));
        AssertBlocks("```js\nlet b = 2;\n\n```\n", new SafeCode("js", "let b = 2;\n", true));
    }

    [Fact]
    public void Task_items_keep_their_box_and_state() =>
        AssertBlocks("- [x] Eta done\n- [ ] Theta open\n  - nested under theta\n",
            new SafeList(false,
            [
                new SafeListItem(true, true, [T("Eta done")], []),
                new SafeListItem(true, false, [T("Theta open")], [Bullets(Item([T("nested under theta")]))]),
            ]));

    [Fact]
    public void Indented_code_has_no_trailing_line_break() =>
        AssertBlocks("    let value = 1;\n", new SafeCode("", "let value = 1;", true));

    [Theory]
    [InlineData("[x]")]
    [InlineData("[ ]")]
    [InlineData("[X]")]
    public void Bare_task_markers_stay_literal_list_text(string marker) =>
        AssertBlocks($"- {marker}\n", Bullets(Item([T(marker)])));

    [Fact]
    public void A_fence_still_streaming_is_not_complete() =>
        AssertBlocks("```cs\nvar x = 1;", new SafeCode("cs", "var x = 1;", false));

    [Fact]
    public void Unsafe_links_become_their_text() =>
        AssertBlocks("[click](javascript:alert(1))", P(T("click")));

    [Fact]
    public void Tables_keep_headers_and_cells() =>
        AssertBlocks("| a | b |\n|---|---|\n| 1 | **2** |\n",
            new SafeTable([[T("a")], [T("b")]], [[[T("1")], [new SafeStrong([T("2")])]]]));

    [Fact]
    public void Headings_rules_strike_and_breaks() =>
        AssertBlocks("## Title\n\n---\n\n~~old~~ new\nnext line",
            new SafeHeading(2, [T("Title")]),
            new SafeRule(),
            P(new SafeStrike([T("old")]), T(" new\nnext line")));
}
