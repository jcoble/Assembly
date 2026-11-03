using Assembly.Infrastructure.Chat.Files;

namespace Assembly.Tests.Draft.Files;

public sealed class FileTrashTests
{
    [Fact]
    public async Task An_unavailable_trash_runner_never_deletes_the_file()
    {
        using var folder = new GitFolder("a");
        var calls = new List<(string Executable, IReadOnlyList<string> Args, string? Input)>();
        var trash = new FileTrash((exe, args, input, _) => { calls.Add((exe, args, input)); return Task.FromResult(false); });
        var result = await trash.TrashAsync(Path.Combine(folder.Root, "a"), CancellationToken.None);
        Assert.Equal("trash_unavailable", result.Error!.Code);
        Assert.True(File.Exists(Path.Combine(folder.Root, "a")));
        var call = Assert.Single(calls);
        if (OperatingSystem.IsMacOS()) { Assert.Equal("/usr/bin/osascript", call.Executable); Assert.Equal(Path.Combine(folder.Root, "a"), call.Args[^1]); }
        else { Assert.Equal("gio", call.Executable); Assert.Equal(["trash", "--", Path.Combine(folder.Root, "a")], call.Args); }
    }

    [Theory]
    [InlineData("quote'file")]
    [InlineData("line\nbreak")]
    [InlineData("-leading-dash")]
    public async Task A_path_is_an_argument_never_part_of_the_trash_script(string name)
    {
        using var folder = new GitFolder();
        var path = Path.Combine(folder.Root, name);
        await File.WriteAllTextAsync(path, "keep");
        var trash = new FileTrash((_, args, input, _) =>
        {
            Assert.Equal(path, args[^1]);
            if (input is not null) Assert.DoesNotContain(path, input);
            return Task.FromResult(false);
        });
        await trash.TrashAsync(path, CancellationToken.None);
        Assert.Equal("keep", await File.ReadAllTextAsync(path));
    }
}
