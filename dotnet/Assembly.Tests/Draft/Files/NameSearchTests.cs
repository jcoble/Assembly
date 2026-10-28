using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class NameSearchTests
{
    private static readonly string[] Tree =
        [".gitignore", "src/Program.cs", "src/program_tests.cs", "docs/PROGRAM.md", "bin/program.dll", "programs/a.txt", "readme.md"];

    // Pins: files#7
    [Fact]
    public async Task Names_match_without_case_folders_included_ignored_files_left_out()
    {
        using var folder = new GitFolder(Tree);
        var page = await NameSearch.PageAsync(folder.Root, "PROGRAM", 0, 50, CancellationToken.None);
        Assert.True(page.Complete);
        Assert.Null(page.NextCursor);
        Assert.Equal(["docs/PROGRAM.md", "programs/", "src/Program.cs", "src/program_tests.cs"],
            page.Matches.Select(match => match.RelativePath + (match.IsDirectory ? "/" : "")).Order(StringComparer.Ordinal));
        Assert.All(page.Matches, match => Assert.Equal(match.RelativePath, match.Path));
        Assert.Contains(page.Matches, match => match is { Name: "Program.cs", RelativePath: "src/Program.cs" });
    }

    // Pins: files#9
    [Fact]
    public async Task Pages_follow_the_cursor_without_repeats_until_complete()
    {
        using var folder = new GitFolder(Tree);
        var first = await NameSearch.PageAsync(folder.Root, "program", 0, 3, CancellationToken.None);
        Assert.Equal(3, first.Matches.Count);
        Assert.False(first.Complete);
        Assert.Equal(3, first.NextCursor);
        var second = await NameSearch.PageAsync(folder.Root, "program", first.NextCursor!.Value, 3, CancellationToken.None);
        Assert.True(second.Complete);
        Assert.Null(second.NextCursor);
        Assert.Equal(4, first.Matches.Concat(second.Matches).Select(match => match.RelativePath).Distinct().Count());
    }

    // Pins: files#7
    [Fact]
    public async Task A_search_with_nothing_to_find_is_complete_and_empty()
    {
        using var folder = new GitFolder(Tree);
        var page = await NameSearch.PageAsync(folder.Root, "nothing-like-it", 0, 50, CancellationToken.None);
        Assert.Empty(page.Matches);
        Assert.True(page.Complete);
    }

    // Pins: files#8
    [Fact]
    public async Task A_cancelled_search_stops()
    {
        using var folder = new GitFolder(Tree);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NameSearch.PageAsync(folder.Root, "program", 0, 50, new CancellationToken(canceled: true)));
    }
}
