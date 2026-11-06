using System.Text.Json;
using Assembly.Application.Features.Chat.Rows.Context;
using Assembly.Domain.Rows;

namespace Assembly.Tests.Features.Chat.Rows;

/// <summary>The paths a prompt names and the images it attaches.</summary>
public sealed class PromptPathsTests
{
    public static TheoryData<string, string?, string?> SharedLines()
    {
        var lines = new TheoryData<string, string?, string?>();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "attached-image-lines.json")));
        foreach (var line in fixture.RootElement.EnumerateArray())
            lines.Add(line.GetProperty("line").GetString()!, line.GetProperty("path").GetString(), line.GetProperty("name").GetString());
        return lines;
    }

    // Pins: g0-shape#13
    [Theory]
    [MemberData(nameof(SharedLines))]
    public void A_shared_line_reads_as_the_fixture_says(string line, string? path, string? name)
    {
        var attached = PromptPaths.Of(line).Attached;
        if (path is null) Assert.Empty(attached);
        else Assert.Equal(new AttachedImage(path, name!), Assert.Single(attached));
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_dropped_quoted_path_is_one_path() =>
        Assert.Equal(["/my docs/a.md"], PromptPaths.Of("read \"/my docs/a.md\" please").Named);

    // Pins: g0-shape#13
    [Fact]
    public void Surrounding_punctuation_is_stripped() =>
        Assert.Equal(["/a/b.md"], PromptPaths.Of("see (/a/b.md), thanks").Named);

    // Pins: g0-shape#13
    [Fact]
    public void A_home_path_stays_as_written_and_backticks_go() =>
        Assert.Equal(["~/x/y.md"], PromptPaths.Of("see `~/x/y.md`.").Named);

    // Pins: g0-shape#13
    [Fact]
    public void A_web_address_and_a_relative_word_are_left_out() =>
        Assert.Empty(PromptPaths.Of("see https://x/y and file:///a/b and a/b.md and ./c.md and ../d.md").Named);

    // Pins: g0-shape#13
    [Fact]
    public void An_attached_lines_paths_are_not_named()
    {
        var (named, attached) = PromptPaths.Of("look at /a.md\n\nAttached image: /s/a.png (a.png) thumbnail: /s/t.webp\nAttached image: /s/b.png (b.png)");
        Assert.Equal(["/a.md"], named);
        Assert.Equal([new AttachedImage("/s/a.png", "a.png"), new AttachedImage("/s/b.png", "b.png")], attached);
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_prompt_names_at_most_twenty_paths()
    {
        var named = PromptPaths.Of(string.Join(' ', Enumerable.Range(0, 21).Select(i => $"/p/{i}.md"))).Named;
        Assert.Equal(20, named.Count);
        Assert.Equal("/p/19.md", named[^1]);
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_repeated_path_counts_once_toward_the_twenty()
    {
        var named = PromptPaths.Of(string.Join(' ', Enumerable.Range(0, 21).Select(i => $"/p/{i}.md /p/{i}.md"))).Named;
        Assert.Equal(20, named.Count);
        Assert.Equal(20, named.Distinct().Count());
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_line_in_the_middle_of_a_prompt_is_not_an_attachment_and_its_path_is_named()
    {
        var (named, attached) = PromptPaths.Of("Attached image: /a.png (x.png)\nis how the composer writes it");
        Assert.Empty(attached);
        Assert.Equal(["/a.png"], named);
    }

    // Pins: g0-shape#13
    [Fact]
    public void Text_with_no_paths_gives_empty_lists()
    {
        var (named, attached) = PromptPaths.Of("hello there");
        Assert.Empty(named);
        Assert.Empty(attached);
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_quoted_phrase_does_not_hide_the_path_inside_it() =>
        Assert.Equal(["/a.md"], PromptPaths.Of("he said \"read /a.md first\" ok").Named);

    // Pins: g0-shape#13
    [Fact]
    public void A_stray_quote_hides_nothing_after_it() =>
        Assert.Equal(["/a.md", "/b.md"], PromptPaths.Of("a 6\" screen, see /a.md and /b.md").Named);

    // Pins: g0-shape#13
    [Fact]
    public void A_quote_opening_a_word_that_never_closes_is_literal() =>
        Assert.Equal(["/a.md", "/b.md"], PromptPaths.Of("see \"/a.md and /b.md").Named);

    // Pins: g0-shape#13
    [Fact]
    public void A_quote_does_not_close_on_a_later_line() =>
        Assert.Equal(["/a.md", "/b.md"], PromptPaths.Of("see \"/a.md\nand /b.md\" ok").Named);

    // Pins: g0-shape#13
    [Fact]
    public void A_home_path_with_a_space_groups_too() =>
        Assert.Equal(["~/my docs/a.md"], PromptPaths.Of("see \"~/my docs/a.md\" ok").Named);

    // Pins: g0-shape#13
    [Fact]
    public void Punctuation_around_a_quoted_path_goes_and_the_group_holds() =>
        Assert.Equal(["/my docs/a.md"], PromptPaths.Of("see (\"/my docs/a.md\"), ok").Named);

    // These two read in one pass; a copy per quote would hang the run here.
    // Pins: g0-shape#13
    [Fact]
    public void A_megabyte_of_minified_json_reads_and_the_path_after_it_is_named()
    {
        var json = "{" + string.Concat(Enumerable.Repeat("\"key\":\"value\",", 70_000)) + "}";
        Assert.Equal(["/real/path.md"], PromptPaths.Of($"{json} see /real/path.md").Named);
    }

    // Pins: g0-shape#13
    [Fact]
    public void A_megabyte_of_quotes_reads_and_the_path_after_it_is_named() =>
        Assert.Equal(["/real/path.md"], PromptPaths.Of($"{new string('"', 1_000_000)} see /real/path.md").Named);
}
