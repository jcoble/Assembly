using System.Security.Cryptography;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class FileSaveTests
{
    [Fact]
    public async Task A_changed_file_conflicts_without_overwriting_external_bytes()
    {
        using var folder = new GitFolder();
        var path = Path.Combine(folder.Root, "a");
        await File.WriteAllTextAsync(path, "old");
        var revision = await FileSave.RevisionAsync(path, CancellationToken.None);
        await File.WriteAllTextAsync(path, "external");
        var result = await FileSave.ReplaceAsync(folder.Root, "a", revision, "draft"u8.ToArray(), CancellationToken.None);
        Assert.Equal("conflict", result.State);
        Assert.Equal("external", await File.ReadAllTextAsync(path));
        Assert.Equal(["a"], Directory.GetFileSystemEntries(folder.Root).Select(Path.GetFileName).Where(name => name != ".git"));
    }

    [Fact]
    public async Task Successful_save_preserves_exact_bytes_and_permissions()
    {
        using var folder = new GitFolder();
        var path = Path.Combine(folder.Root, "a");
        await File.WriteAllTextAsync(path, "old");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var revision = await FileSave.RevisionAsync(path, CancellationToken.None);
        var bytes = "é\r\nlast\r\n"u8.ToArray();
        var result = await FileSave.ReplaceAsync(folder.Root, "a", revision, bytes, CancellationToken.None);
        Assert.Equal("saved", result.State);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Revision);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task A_change_during_staging_is_rechecked_and_the_private_stage_is_removed()
    {
        using var folder = new GitFolder();
        var path = Path.Combine(folder.Root, "a");
        await File.WriteAllTextAsync(path, "old");
        var revision = await FileSave.RevisionAsync(path, CancellationToken.None);
        var save = new FileSave(() => File.WriteAllTextAsync(path, "external"));
        var result = await save.ReplaceCoreAsync(folder.Root, "a", revision, "draft"u8.ToArray(), CancellationToken.None);
        Assert.Equal("conflict", result.State);
        Assert.Equal("external", await File.ReadAllTextAsync(path));
        Assert.Equal(["a"], Directory.GetFileSystemEntries(folder.Root).Select(Path.GetFileName).Where(name => name != ".git"));
    }

    [Fact]
    public async Task A_missing_target_is_not_recreated_and_invalid_utf8_does_not_change_disk()
    {
        using var folder = new GitFolder();
        var missing = await FileSave.ReplaceAsync(folder.Root, "missing", "old", "draft"u8.ToArray(), CancellationToken.None);
        Assert.Equal("conflict", missing.State);
        Assert.False(File.Exists(Path.Combine(folder.Root, "missing")));
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "a"), "old");
        var revision = await FileSave.RevisionAsync(Path.Combine(folder.Root, "a"), CancellationToken.None);
        var invalid = await FileSave.ReplaceAsync(folder.Root, "a", revision, new byte[] { 0xff }, CancellationToken.None);
        Assert.Equal("invalid_text", invalid.Error!.Code);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(folder.Root, "a")));
    }
}
