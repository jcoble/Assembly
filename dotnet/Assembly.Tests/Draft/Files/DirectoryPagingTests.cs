using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class DirectoryPagingTests
{
    [Fact]
    public async Task Every_sibling_is_reachable_once_in_sorted_bounded_pages()
    {
        using var folder = new GitFolder();
        for (var i = 0; i < 2501; i++) await File.WriteAllTextAsync(Path.Combine(folder.Root, $"file-{i:D4}"), "x");
        using var lease = DirectoryGeneration.Shared.Observe(folder.Root);
        PageCursor? cursor = null;
        var names = new List<string>();
        do
        {
            var page = await DirectoryListing.ListAsync(folder.Root, "", false, CancellationToken.None, cursor: cursor);
            Assert.InRange(page.Entries.Count, 1, 500);
            names.AddRange(page.Entries.Select(entry => entry.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(2501, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task A_changed_generation_refuses_the_old_continuation()
    {
        using var folder = new GitFolder();
        for (var i = 0; i < 501; i++) File.WriteAllText(Path.Combine(folder.Root, $"file-{i:D4}"), "x");
        using var lease = DirectoryGeneration.Shared.Observe(folder.Root);
        var first = await DirectoryListing.ListAsync(folder.Root, "", false, CancellationToken.None);
        var changed = lease.ChangedAsync(first.NextCursor!.Generation, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "new-file"), "new");
        await changed.WaitAsync(TimeSpan.FromSeconds(10));
        var failed = await Assert.ThrowsAsync<CallFailed>(() => DirectoryListing.ListAsync(folder.Root, "", false,
            CancellationToken.None, cursor: first.NextCursor));
        Assert.Contains("cursor_changed", failed.Message);
    }
    [Fact]
    public async Task Descending_names_keep_folders_first_and_excluded_toggle_preserves_marking()
    {
        using var folder = new GitFolder("folder-a/", "folder-z/", "a", "z", ".gitignore", "bin/out");
        var page = await DirectoryListing.ListAsync(folder.Root, "", false, CancellationToken.None, descending: true);
        Assert.Equal(["folder-z", "folder-a", "z", "a", ".gitignore"], page.Entries.Select(entry => entry.Name));
        var excluded = await DirectoryListing.ListAsync(folder.Root, "", true, CancellationToken.None, descending: true);
        Assert.Contains(excluded.Entries, entry => entry is { Name: "bin", Excluded: true });
    }

}
