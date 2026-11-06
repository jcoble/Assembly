using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class FileSaveUploadsTests
{
    [Fact]
    public async Task Upload_chunks_are_owned_ordered_and_commit_once()
    {
        using var folder = new GitFolder();
        var path = Path.Combine(folder.Root, "a");
        await File.WriteAllTextAsync(path, "old");
        var uploads = new FileSaveUploads();
        var revision = await FileSave.RevisionAsync(path, CancellationToken.None);
        var begun = await uploads.BeginAsync(folder.Root, new BeginSave("s", "a", revision, 5), "c", CancellationToken.None);
        await Assert.ThrowsAsync<CallFailed>(() => uploads.AppendAsync(new SaveChunk(begun.Upload, 0, "draft"u8.ToArray()), "other", CancellationToken.None));
        await uploads.AppendAsync(new SaveChunk(begun.Upload, 0, "dr"u8.ToArray()), "c", CancellationToken.None);
        await Assert.ThrowsAsync<CallFailed>(() => uploads.AppendAsync(new SaveChunk(begun.Upload, 0, "aft"u8.ToArray()), "c", CancellationToken.None));
        // An invalid same-owner chunk aborts its upload; the file remains untouched.
        Assert.Equal("old", await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<CallFailed>(() => uploads.CommitAsync(begun.Upload, "c", CancellationToken.None));
        begun = await uploads.BeginAsync(folder.Root, new BeginSave("s", "a", revision, 5), "c", CancellationToken.None);
        await uploads.AppendAsync(new SaveChunk(begun.Upload, 0, "draft"u8.ToArray()), "c", CancellationToken.None);
        var saved = await uploads.CommitAsync(begun.Upload, "c", CancellationToken.None);
        Assert.Equal("saved", saved.State);
        Assert.Equal("draft", await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<CallFailed>(() => uploads.CommitAsync(begun.Upload, "c", CancellationToken.None));
    }

    [Fact]
    public async Task Close_deletes_only_its_private_stages_and_refuses_later_commit()
    {
        using var folder = new GitFolder();
        var path = Path.Combine(folder.Root, "a");
        await File.WriteAllTextAsync(path, "old");
        var uploads = new FileSaveUploads();
        var revision = await FileSave.RevisionAsync(path, CancellationToken.None);
        var begun = await uploads.BeginAsync(folder.Root, new BeginSave("s", "a", revision, 3), "c", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder.Root, ".assembly-save-unrelated"), "keep");
        await uploads.AppendAsync(new SaveChunk(begun.Upload, 0, "new"u8.ToArray()), "c", CancellationToken.None);
        uploads.Close("c");
        await Assert.ThrowsAsync<CallFailed>(() => uploads.CommitAsync(begun.Upload, "c", CancellationToken.None));
        Assert.Equal("old", await File.ReadAllTextAsync(path));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(folder.Root, ".assembly-save-unrelated")));
        Assert.Single(Directory.GetFiles(folder.Root, ".assembly-save-*"));
    }
}
