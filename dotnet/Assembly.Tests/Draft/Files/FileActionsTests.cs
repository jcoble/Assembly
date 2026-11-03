using Assembly.Application.Common.Calls;
using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class FileActionsTests
{
    [Fact]
    public async Task Collision_keeps_both_files_and_successful_copy_keeps_bytes_and_permissions()
    {
        using var folder = new GitFolder("a", "b");
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "a"), "source");
        await File.WriteAllTextAsync(Path.Combine(folder.Root, "b"), "target");
        var collision = await FileActions.ExecuteAsync(folder.Root, new FileActionArgs("s", "rename", "a", "b", null), CancellationToken.None);
        Assert.Equal("collision", collision.Error!.Code);
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(folder.Root, "a")));
        Assert.Equal("target", await File.ReadAllTextAsync(Path.Combine(folder.Root, "b")));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(folder.Root, "a"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var copy = await FileActions.ExecuteAsync(folder.Root, new FileActionArgs("s", "copy", "a", "c", null), CancellationToken.None);
        Assert.Equal("applied", copy.State);
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(folder.Root, "c")));
        if (!OperatingSystem.IsWindows()) Assert.Equal(File.GetUnixFileMode(Path.Combine(folder.Root, "a")), File.GetUnixFileMode(Path.Combine(folder.Root, "c")));
    }

    [Fact]
    public async Task Root_mutations_symlinks_and_copying_into_oneself_are_refused()
    {
        using var folder = new GitFolder("src/a");
        Directory.CreateSymbolicLink(Path.Combine(folder.Root, "link"), Path.Combine(folder.Root, "src"));
        await Assert.ThrowsAsync<CallFailed>(() => FileActions.ExecuteAsync(folder.Root, new FileActionArgs("s", "move", "", "other", null), CancellationToken.None));
        await Assert.ThrowsAsync<CallFailed>(() => FileActions.ExecuteAsync(folder.Root, new FileActionArgs("s", "copy", "link/a", "copy", null), CancellationToken.None));
        var inside = await FileActions.ExecuteAsync(folder.Root, new FileActionArgs("s", "copy", "src", "src/child", null), CancellationToken.None);
        Assert.Equal("invalid_destination", inside.Error!.Code);
        Assert.True(File.Exists(Path.Combine(folder.Root, "src/a")));
        Assert.False(Directory.Exists(Path.Combine(folder.Root, "src/child")));
    }

    [Fact]
    public async Task Cancelled_copy_leaves_only_the_original_and_unrelated_stage()
    {
        using var folder = new GitFolder("src/a");
        File.WriteAllText(Path.Combine(folder.Root, ".assembly-action-unrelated"), "keep");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileActions.ExecuteAsync(folder.Root,
            new FileActionArgs("s", "copy", "src", "copy", null), new CancellationToken(true)));
        Assert.False(Directory.Exists(Path.Combine(folder.Root, "copy")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder.Root, ".assembly-action-unrelated")));
    }
}
