using Assembly.Infrastructure.Git;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class NameSearchTests
{
    private static async Task<SearchResult> PageAsync(string root, string query, int pageSize, CancellationToken ct)
    {
        using var search = new SearchRun();
        return await search.PageAsync(root, new SearchArgs("s", query, PageSize: pageSize), "c", ct);
    }

    private static readonly string[] Tree =
        [".gitignore", "src/Program.cs", "src/program_tests.cs", "docs/PROGRAM.md", "bin/program.dll", "programs/a.txt", "readme.md"];

    // Pins: files#7
    [Fact]
    public async Task Names_match_without_case_folders_included_ignored_files_left_out()
    {
        using var folder = new GitFolder(Tree);
        var page = await PageAsync(folder.Root, "PROGRAM", 50, CancellationToken.None);
        Assert.False(page.More);
        Assert.Equal(["docs/PROGRAM.md", "programs/", "src/Program.cs", "src/program_tests.cs"],
            page.Names.Select(match => match.RelativePath + (match.IsDirectory ? "/" : "")).Order(StringComparer.Ordinal));
        Assert.All(page.Names, match => Assert.Equal(match.RelativePath, match.Path));
        Assert.Contains(page.Names, match => match is { Name: "Program.cs", RelativePath: "src/Program.cs" });
    }

    // Pins: files#7
    [Fact]
    public async Task Files_inside_a_submodule_are_found()
    {
        using var inner = new GitFolder("needle.txt");
        GitFolder.Commit(inner.Root);
        using var folder = new GitFolder(Tree);
        GitFolder.Run(folder.Root, "-c", "protocol.file.allow=always", "submodule", "add", "--quiet", inner.Root, "sub");
        var page = await PageAsync(folder.Root, "needle", 50, CancellationToken.None);
        Assert.Equal(["sub/needle.txt"], page.Names.Select(match => match.RelativePath));
    }

    // Pins: files#9
    [Fact]
    public async Task Pages_follow_the_cursor_without_repeats_until_complete()
    {
        using var folder = new GitFolder(Tree);
        using var search = new SearchRun();
        var args = new SearchArgs("s", "program", PageSize: 3);
        var first = await search.PageAsync(folder.Root, args, "c", CancellationToken.None);
        Assert.Equal(3, first.Names.Count);
        Assert.True(first.More);
        var second = await search.PageAsync(folder.Root, args with { SearchId = first.SearchId }, "c", CancellationToken.None);
        Assert.False(second.More);
        Assert.Equal(4, first.Names.Concat(second.Names).Select(match => match.RelativePath).Distinct().Count());
    }

    // Pins: files#7
    [Fact]
    public async Task A_file_deleted_from_disk_but_still_in_git_s_index_isn_t_found()
    {
        using var folder = new GitFolder(Tree);
        GitFolder.Commit(folder.Root);
        File.Delete(Path.Combine(folder.Root, "docs", "PROGRAM.md"));
        var page = await PageAsync(folder.Root, "program", 50, CancellationToken.None);
        Assert.DoesNotContain(page.Names, match => match.Path.StartsWith("docs", StringComparison.Ordinal));
        Assert.Equal(3, page.Names.Count);
    }

    // Pins: files#7
    [Fact]
    public async Task A_search_with_nothing_to_find_is_complete_and_empty()
    {
        using var folder = new GitFolder(Tree);
        var page = await PageAsync(folder.Root, "nothing-like-it", 50, CancellationToken.None);
        Assert.Empty(page.Names);
        Assert.False(page.More);
    }

    // Pins: files#8
    [Fact]
    public async Task A_cancelled_search_stops()
    {
        using var folder = new GitFolder(Tree);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PageAsync(folder.Root, "program", 50, new CancellationToken(canceled: true)));
    }

    // Pins: files#8
    [Fact]
    public async Task A_walk_cancelled_partway_stops_and_says_so()
    {
        using var folder = new GitFolder([.. Enumerable.Range(0, 3000).Select(i => $"d{i % 30}/f{i:D4}.txt")]);
        using var cancel = new CancellationTokenSource();
        var read = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new GitCli().ReadAsync(folder.Root, ["ls-files", "-z", "--others"], null, cancel.Token))
            {
                if (++read == 1)
                    await cancel.CancelAsync();
            }
        });
        Assert.Equal(1, read);
    }
}
