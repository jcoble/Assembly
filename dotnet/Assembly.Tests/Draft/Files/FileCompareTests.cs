using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class FileCompareTests
{
    [Fact]
    public async Task Compare_reads_current_disk_preserves_draft_stage_and_never_writes_the_target()
    {
        using var folder = new GitFolder();
        var path = Path.Combine(folder.Root, "a");
        await File.WriteAllTextAsync(path, "base\n");
        var uploads = new FileSaveUploads();
        var revision = await FileSave.RevisionAsync(path, default);
        var begun = await uploads.BeginAsync(folder.Root, new BeginSave("s", "a", revision, 6), "c", default);
        await uploads.AppendAsync(new SaveChunk(begun.Upload, 0, "draft\n"u8.ToArray()), "c", default);
        await File.WriteAllTextAsync(path, "external\n");
        await Assert.ThrowsAsync<CallFailed>(() => uploads.CompareAsync(begun.Upload, "other", default));
        var compared = await uploads.CompareAsync(begun.Upload, "c", default);
        Assert.Equal(await FileSave.RevisionAsync(path, default), compared.DiskRevision);
        Assert.Contains("-external", compared.Diff.Unstaged);
        Assert.Contains("+draft", compared.Diff.Unstaged);
        Assert.Equal("external\n", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(folder.Root, ".assembly-save-*"));
        // Comparison is repeatable while the owning connection retains the same private upload.
        Assert.Equal(compared, await uploads.CompareAsync(begun.Upload, "c", default));
        uploads.Drop(begun.Upload, "c");
        Assert.Empty(Directory.GetFiles(folder.Root, ".assembly-save-*"));
    }

    [Fact]
    public async Task Deleted_and_empty_disk_versions_stay_distinct_and_missing_commit_never_recreates()
    {
        using var folder = new GitFolder();
        var path = Path.Combine(folder.Root, "a");
        var uploads = new FileSaveUploads();
        var begun = await uploads.BeginAsync(folder.Root, new BeginSave("s", "a", new string('0', 64), 0), "c", default);
        var missing = await uploads.CompareAsync(begun.Upload, "c", default);
        Assert.Null(missing.DiskRevision);
        Assert.Contains("new file mode", missing.Diff.Unstaged);
        var result = await uploads.CommitAsync(begun.Upload, "c", default);
        Assert.Equal("conflict", result.State);
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, "");
        begun = await uploads.BeginAsync(folder.Root, new BeginSave("s", "a", new string('0', 64), 0), "c", default);
        var empty = await uploads.CompareAsync(begun.Upload, "c", default);
        Assert.NotNull(empty.DiskRevision);
        Assert.Empty(empty.Diff.Unstaged);
        uploads.Close("c");
    }

    [Fact]
    public async Task Cancellation_or_changed_ownership_ends_only_the_affected_upload()
    {
        using var folder = new GitFolder();
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "a"), "old");
        var uploads = new FileSaveUploads();
        var args = new BeginSave("s", "a", new string('0', 64), 0);
        var first = await uploads.BeginAsync(folder.Root, args, "c", default);
        var second = await uploads.BeginAsync(folder.Root, args, "other", default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploads.CompareAsync(first.Upload, "c", cancellation.Token));
        Assert.Single(Directory.GetFiles(folder.Root, ".assembly-save-*"));
        await Assert.ThrowsAsync<CallFailed>(() => uploads.CompareAsync(second.Upload, "other", default,
            (_, _) => throw new CallFailed("checkout changed")));
        Assert.Empty(Directory.GetFiles(folder.Root, ".assembly-save-*"));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(folder.Root, "a")));
    }
}
