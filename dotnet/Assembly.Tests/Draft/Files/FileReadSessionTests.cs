using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class FileReadSessionTests
{
    [Fact]
    public async Task Continuations_keep_original_bytes_after_an_external_edit_and_release_on_completion()
    {
        using var folder = new GitFolder();
        var original = new string('é', FileRead.ChunkBytes) + "\r\nlast\n";
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "a.txt"), original);
        var reads = new FileReadSession();
        var first = await reads.ReadAsync(folder.Root, "a.txt", 0, null, "c", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "a.txt"), "external");
        var text = first.Content;
        var next = first.NextOffset;
        while (next is { } offset)
        {
            var part = await reads.ReadAsync(folder.Root, "a.txt", offset, first.Revision, "c", CancellationToken.None);
            Assert.Equal(first.Revision, part.Revision);
            text += part.Content;
            next = part.NextOffset;
        }
        Assert.Equal(original, text);
        Assert.Equal(0, reads.HeldBytes);
    }

    [Fact]
    public async Task A_snapshot_belongs_to_its_connection_and_path_and_close_releases_it()
    {
        using var folder = new GitFolder();
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "a"), new string('x', FileRead.ChunkBytes + 1));
        var reads = new FileReadSession();
        var first = await reads.ReadAsync(folder.Root, "a", 0, null, "one", CancellationToken.None);
        await Assert.ThrowsAsync<CallFailed>(() => reads.ReadAsync(folder.Root, "a", first.NextOffset!.Value,
            first.Revision, "two", CancellationToken.None));
        Assert.True(reads.HeldBytes > 0);
        reads.Close("two");
        Assert.True(reads.HeldBytes > 0);
        reads.Close("one");
        Assert.Equal(0, reads.HeldBytes);
    }

    [Fact]
    public async Task Wrong_revision_or_cancelled_continuation_drops_only_its_snapshot()
    {
        using var folder = new GitFolder();
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "a"), new string('x', FileRead.ChunkBytes + 1));
        var reads = new FileReadSession();
        var first = await reads.ReadAsync(folder.Root, "a", 0, null, "c", CancellationToken.None);
        var failure = await Assert.ThrowsAsync<CallFailed>(() => reads.ReadAsync(folder.Root, "a", first.NextOffset!.Value,
            "wrong", "c", CancellationToken.None));
        Assert.Contains("revision_changed", failure.Message);
        Assert.Equal(0, reads.HeldBytes);
        first = await reads.ReadAsync(folder.Root, "a", 0, null, "c", CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reads.ReadAsync(folder.Root, "a", first.NextOffset!.Value,
            first.Revision, "c", new CancellationToken(true)));
        Assert.Equal(0, reads.HeldBytes);
    }

    [Fact]
    public async Task Four_unfinished_snapshots_are_the_connection_limit()
    {
        using var folder = new GitFolder();
        var reads = new FileReadSession();
        for (var i = 0; i < 5; i++)
            await File.WriteAllTextAsync(Path.Combine(folder.Root, i.ToString()), new string('x', FileRead.ChunkBytes + 1));
        for (var i = 0; i < 4; i++)
            await reads.ReadAsync(folder.Root, i.ToString(), 0, null, "c", CancellationToken.None);
        await Assert.ThrowsAsync<CallFailed>(() => reads.ReadAsync(folder.Root, "4", 0, null, "c", CancellationToken.None));
        reads.Close("c");
        Assert.Equal(0, reads.HeldBytes);
    }
}
