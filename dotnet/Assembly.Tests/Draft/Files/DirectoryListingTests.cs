using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class DirectoryListingTests
{
    private static readonly string[] Tree = [".gitignore", "a.txt", "b.log", "Z.md", "src/Program.cs", "bin/app.dll", "docs/"];

    private static string Shape(FileEntry entry) => $"{entry.Path}{(entry.IsDirectory ? "/" : "")}{(entry.Excluded ? " (excluded)" : "")}";

    // Pins: files#1
    [Fact]
    public async Task A_folder_lists_folders_first_then_files_by_name_without_ignored_entries()
    {
        using var folder = new GitFolder(Tree);
        var listing = await DirectoryListing.ListAsync(folder.Root, "", includeExcluded: false, CancellationToken.None);
        Assert.Equal(["docs/", "src/", ".gitignore", "a.txt", "Z.md"], listing.Entries.Select(Shape));
        Assert.False(listing.Truncated);
    }

    // Pins: files#3
    [Fact]
    public async Task Ignored_entries_and_the_git_folder_are_marked_excluded_when_asked_for()
    {
        using var folder = new GitFolder(Tree);
        var listing = await DirectoryListing.ListAsync(folder.Root, ".", includeExcluded: true, CancellationToken.None);
        Assert.Equal([".git/ (excluded)", "bin/ (excluded)", "docs/", "src/", ".gitignore", "a.txt", "b.log (excluded)", "Z.md"],
            listing.Entries.Select(Shape));
    }

    // Pins: files#1
    [Fact]
    public async Task A_subfolder_s_entries_carry_paths_from_the_root()
    {
        using var folder = new GitFolder(Tree);
        var listing = await DirectoryListing.ListAsync(folder.Root, "src", includeExcluded: false, CancellationToken.None);
        var entry = Assert.Single(listing.Entries);
        Assert.Equal(("src/Program.cs", "Program.cs", false), (entry.Path, entry.Name, entry.IsDirectory));
    }

    // Pins: files#10
    [Fact]
    public async Task A_link_is_listed_but_never_as_a_folder()
    {
        using var folder = new GitFolder("src/");
        Directory.CreateSymbolicLink(Path.Combine(folder.Root, "up"), Path.GetTempPath());
        var listing = await DirectoryListing.ListAsync(folder.Root, "", includeExcluded: false, CancellationToken.None);
        Assert.Equal(["src/", "up"], listing.Entries.Select(Shape));
    }

    // Pins: files#4
    [Fact]
    public async Task A_huge_folder_stops_at_the_limit_and_says_so()
    {
        using var folder = new GitFolder();
        for (var i = 0; i <= DirectoryListing.Limit; i++)
            File.WriteAllText(Path.Combine(folder.Root, $"f{i:D5}"), "");
        var listing = await DirectoryListing.ListAsync(folder.Root, "", includeExcluded: false, CancellationToken.None);
        Assert.Equal(DirectoryListing.Limit, listing.Entries.Count);
        Assert.True(listing.Truncated);
    }

    // Pins: files#2
    [Fact]
    public async Task A_cancelled_listing_stops()
    {
        using var folder = new GitFolder(Tree);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DirectoryListing.ListAsync(folder.Root, "", includeExcluded: false, new CancellationToken(canceled: true)));
    }
}
