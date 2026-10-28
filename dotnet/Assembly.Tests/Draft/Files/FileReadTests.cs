using System.Security.Cryptography;
using System.Text;
using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class FileReadTests
{
    private static string Write(GitFolder folder, string path, byte[] bytes)
    {
        var full = Path.Combine(folder.Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    // Pins: files#11
    [Fact]
    public async Task A_small_text_file_reads_whole_with_its_size_revision_and_lines()
    {
        using var folder = new GitFolder();
        var bytes = Encoding.UTF8.GetBytes("one\ntwo é\nthree");
        var revision = Write(folder, "src/a.txt", bytes);
        var chunk = await FileRead.ReadAsync(folder.Root, "src/a.txt", 0, CancellationToken.None);
        Assert.Equal(("one\ntwo é\nthree", (long)bytes.Length, revision, 3, false, (long?)null),
            (chunk.Content, chunk.Size, chunk.Revision, chunk.LineCount, chunk.Binary, chunk.NextOffset));
    }

    // Pins: files#11
    [Fact]
    public async Task An_empty_file_has_no_lines_and_a_file_ending_in_a_newline_counts_no_extra_line()
    {
        using var folder = new GitFolder();
        Write(folder, "empty", []);
        Write(folder, "two", "a\nb\n"u8.ToArray());
        Assert.Equal(0, (await FileRead.ReadAsync(folder.Root, "empty", 0, CancellationToken.None)).LineCount);
        Assert.Equal(2, (await FileRead.ReadAsync(folder.Root, "two", 0, CancellationToken.None)).LineCount);
    }

    // Pins: files#13
    [Theory]
    [InlineData(new byte[] { (byte)'a', 0, (byte)'b' })]
    [InlineData(new byte[] { (byte)'a', 0xC3, 0x28 })]
    public async Task A_file_with_a_nul_byte_or_bytes_that_are_not_utf8_is_binary_and_shows_nothing(byte[] bytes)
    {
        using var folder = new GitFolder();
        var revision = Write(folder, "blob", bytes);
        var chunk = await FileRead.ReadAsync(folder.Root, "blob", 0, CancellationToken.None);
        Assert.Equal(("", true, 0, (long?)null, revision), (chunk.Content, chunk.Binary, chunk.LineCount, chunk.NextOffset, chunk.Revision));
    }

    // Pins: files#12
    [Fact]
    public async Task A_large_file_comes_in_parts_that_end_at_line_ends_and_join_to_the_file()
    {
        using var folder = new GitFolder();
        var text = string.Concat(Enumerable.Range(0, 30_000).Select(i => $"line {i} ünïcode padding padding\n"));
        Write(folder, "big.txt", Encoding.UTF8.GetBytes(text));
        var parts = new List<FileChunk>();
        long? offset = 0;
        while (offset is { } at)
        {
            var chunk = await FileRead.ReadAsync(folder.Root, "big.txt", at, CancellationToken.None);
            parts.Add(chunk);
            offset = chunk.NextOffset;
        }
        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.True(Encoding.UTF8.GetByteCount(part.Content) <= FileRead.ChunkBytes));
        Assert.All(parts, part => Assert.EndsWith("\n", part.Content));
        Assert.Equal(text, string.Concat(parts.Select(part => part.Content)));
        Assert.All(parts, part => Assert.Equal(30_000, part.LineCount));
    }

    // Pins: files#12
    [Fact]
    public async Task One_line_longer_than_a_part_splits_between_characters()
    {
        using var folder = new GitFolder();
        var text = new string('é', FileRead.ChunkBytes);
        Write(folder, "long.txt", Encoding.UTF8.GetBytes(text));
        var first = await FileRead.ReadAsync(folder.Root, "long.txt", 0, CancellationToken.None);
        var second = await FileRead.ReadAsync(folder.Root, "long.txt", first.NextOffset!.Value, CancellationToken.None);
        Assert.Null(second.NextOffset);
        Assert.Equal(text, first.Content + second.Content);
    }

    // Pins: files#12
    [Fact]
    public async Task A_file_past_the_limit_is_refused_with_its_size()
    {
        using var folder = new GitFolder();
        Write(folder, "huge.bin", new byte[FileRead.MaxBytes + 1]);
        var failed = await Assert.ThrowsAsync<CallFailed>(() => FileRead.ReadAsync(folder.Root, "huge.bin", 0, CancellationToken.None));
        Assert.Contains("8 MiB", failed.Message);
    }

    // Pins: files#10
    [Theory]
    [InlineData("src", "that isn't a file")]
    [InlineData("missing.txt", "that file isn't there")]
    [InlineData("../outside.txt", "outside the session's folder")]
    [InlineData("up/a.txt", "goes through a link")]
    public async Task Folders_missing_files_escapes_and_links_are_refused(string path, string why)
    {
        using var folder = new GitFolder("src/a.txt");
        Directory.CreateSymbolicLink(Path.Combine(folder.Root, "up"), Path.Combine(folder.Root, "src"));
        var failed = await Assert.ThrowsAsync<CallFailed>(() => FileRead.ReadAsync(folder.Root, path, 0, CancellationToken.None));
        Assert.Contains(why, failed.Message);
    }

    // Pins: files#12
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task An_offset_outside_the_file_is_refused(long offset)
    {
        using var folder = new GitFolder();
        Write(folder, "a.txt", "abc"u8.ToArray());
        await Assert.ThrowsAsync<CallFailed>(() => FileRead.ReadAsync(folder.Root, "a.txt", offset, CancellationToken.None));
    }

    // Pins: files#15
    [Fact]
    public async Task A_cancelled_read_stops()
    {
        using var folder = new GitFolder("a.txt");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileRead.ReadAsync(folder.Root, "a.txt", 0, new CancellationToken(canceled: true)));
    }
}
