using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class ContentSearchTests
{
    [Fact]
    public async Task Literal_case_insensitive_content_matches_report_locations_and_skipped_binary_files()
    {
        using var folder = new GitFolder(".gitignore", "bin/ignored");
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "a.txt"), "first\n  Needle here\nNEEDLE again\n");
        await File.WriteAllBytesAsync(Path.Combine(folder.Root, "binary"), new byte[] { 1, 0, 2 });
        using var search = new SearchRun();
        var page = await search.PageAsync(folder.Root, new SearchArgs("s", "needle", "content", false, null, 1), "c", CancellationToken.None);
        var first = Assert.Single(page.Content);
        Assert.Equal(("a.txt", 2, 3, "  Needle here"), (first.Path, first.Line, first.Column, first.Excerpt));
        Assert.True(page.More);
        var next = await search.PageAsync(folder.Root, new SearchArgs("s", "needle", "content", false, page.SearchId, 1), "c", CancellationToken.None);
        Assert.Equal(3, Assert.Single(next.Content).Line);
        Assert.False(next.More);
        Assert.True(next.Skipped >= 1);
        Assert.Empty(next.Names);
    }

    [Fact]
    public async Task Search_continuations_are_connection_owned_and_close_releases_them()
    {
        using var folder = new GitFolder("needle-a", "needle-b");
        using var search = new SearchRun();
        var args = new SearchArgs("s", "needle", "name", false, null, 1);
        var page = await search.PageAsync(folder.Root, args, "one", CancellationToken.None);
        Assert.True(page.More);
        await Assert.ThrowsAsync<CallFailed>(() => search.PageAsync(folder.Root, args with { SearchId = page.SearchId }, "two", CancellationToken.None));
        await search.CloseAsync("one");
        await Assert.ThrowsAsync<CallFailed>(() => search.PageAsync(folder.Root, args with { SearchId = page.SearchId }, "one", CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_folder_is_a_name_result_and_symlink_scans_are_not_followed()
    {
        using var folder = new GitFolder("needle-folder/");
        Directory.CreateSymbolicLink(Path.Combine(folder.Root, "needle-link"), Path.GetTempPath());
        using var search = new SearchRun();
        var page = await search.PageAsync(folder.Root, new SearchArgs("s", "needle", "name", false, null, 100), "c", CancellationToken.None);
        Assert.Equal("needle-folder", Assert.Single(page.Names).Path);
        Assert.False(page.More);
    }
}
