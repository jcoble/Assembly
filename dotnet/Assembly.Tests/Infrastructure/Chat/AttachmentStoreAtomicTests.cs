using Assembly.Application.Common.Images;
using Assembly.Application.Features.Chat.Attachments;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Attachments;

namespace Assembly.Tests.Infrastructure.Chat;

public sealed class AttachmentStoreAtomicTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"assembly-attachment-atomic-{Guid.NewGuid():N}");
    private readonly TaskCompletionSource _writing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _publish = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly byte[] Image = [.. ImageType.All[0].Magic, 1, 2, 3, 4];

    private async Task BeforePublish(CancellationToken ct)
    {
        _writing.TrySetResult();
        await _publish.Task.WaitAsync(ct);
    }

    [Fact]
    public async Task A_save_exposes_only_the_complete_image_after_publication()
    {
        var store = new AttachmentStore(_root, BeforePublish);
        var save = store.SaveAsync("session", ImageType.All[0], Image, CancellationToken.None);
        await _writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var visibleBeforePublish = FinalFiles();
        _publish.TrySetResult();
        var saved = await save;
        Assert.Empty(visibleBeforePublish);
        Assert.Equal(Image, await File.ReadAllBytesAsync(saved));
        Assert.Equal(saved, Assert.Single(FinalFiles()));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "session")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_or_faulted_save_removes_only_its_owned_temporary_file(bool fail)
    {
        var folder = Path.Combine(_root, "session"); Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, $"{Guid.NewGuid()}.png");
        await File.WriteAllBytesAsync(original, Image);
        var unrelated = Path.Combine(folder, "unrelated.tmp"); await File.WriteAllTextAsync(unrelated, "keep");
        using var stop = new CancellationTokenSource();
        var store = new AttachmentStore(_root, async ct =>
        {
            await BeforePublish(ct);
            if (fail) throw new IOException("controlled publication fault");
        });
        var save = store.SaveAsync("session", ImageType.All[0], Image, stop.Token);
        await _writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (fail) _publish.TrySetResult(); else await stop.CancelAsync();
        if (fail) await Assert.ThrowsAsync<IOException>(() => save);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.Equal(new[] { original, unrelated }.Order(), Directory.GetFiles(folder).Order());
        Assert.Equal(Image, await File.ReadAllBytesAsync(original));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
    }

    private string[] FinalFiles() => Directory.GetFiles(Path.Combine(_root, "session"))
        .Where(path => AttachmentStore.TypeOfName(Path.GetFileName(path)) is not null).ToArray();

    [Fact]
    public async Task Read_pieces_reconstruct_the_original_image_with_bounded_bytes_size_and_mime()
    {
        var image = new byte[1024 * 1024 + 17];
        Image.CopyTo(image, 0); image[^1] = 71;
        var store = new AttachmentStore(_root);
        var path = await store.SaveAsync("session", ImageType.All[0], image, CancellationToken.None);
        var first = await store.ReadAsync("session", Path.GetFileName(path), 0, CancellationToken.None);
        var last = await store.ReadAsync("session", Path.GetFileName(path), first.Data.Length, CancellationToken.None);
        var end = await store.ReadAsync("session", Path.GetFileName(path), image.Length, CancellationToken.None);
        Assert.Equal(1024 * 1024, first.Data.Length); Assert.Equal(17, last.Data.Length); Assert.Empty(end.Data);
        Assert.Equal(image, first.Data.Concat(last.Data).ToArray());
        foreach (var piece in new[] { first, last, end })
        {
            Assert.Equal(image.Length, piece.Size); Assert.Equal("image/png", piece.Mime);
        }
    }

    [Theory]
    [InlineData("traversal", "not an attachment name")]
    [InlineData("missing", "no such attachment")]
    [InlineData("negative", "the offset is outside the attachment")]
    [InlineData("past_end", "the offset is outside the attachment")]
    public async Task Read_refuses_invalid_names_missing_images_and_outside_offsets(string failure, string message)
    {
        var store = new AttachmentStore(_root);
        var path = await store.SaveAsync("session", ImageType.All[0], Image, CancellationToken.None);
        var name = failure switch
        {
            "traversal" => "../" + Path.GetFileName(path),
            "missing" => $"{Guid.NewGuid()}.png",
            _ => Path.GetFileName(path)
        };
        var offset = failure == "negative" ? -1 : failure == "past_end" ? Image.Length + 1 : 0;
        var error = await Assert.ThrowsAsync<CallFailed>(() => store.ReadAsync("session", name, offset, CancellationToken.None));
        Assert.Equal(message, error.Message);
        Assert.Equal(Image, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_cancelled_read_stops_without_changing_the_original()
    {
        var store = new AttachmentStore(_root);
        var path = await store.SaveAsync("session", ImageType.All[0], Image, CancellationToken.None);
        using var stop = new CancellationTokenSource(); await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync("session", Path.GetFileName(path), 0, stop.Token));
        Assert.Equal(Image, await File.ReadAllBytesAsync(path));
    }

    public void Dispose()
    {
        _publish.TrySetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
